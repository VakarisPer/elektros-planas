namespace vamos.Services;

/// <summary>
/// Hourly flows summed per month and tariff zone, so every plan combination can be priced without
/// another pass over 8760 hours. Built from an import series (kWh bought from the grid) and an
/// optional export series (kWh given to the grid).
/// </summary>
public sealed class FlowAggregates
{
    // [zoning index (1, 2, 4 zones)][month][zone]
    private readonly double[][,] _import = { new double[12, 5], new double[12, 5], new double[12, 5] };

    /// <summary>Σ import × np per month, EUR without VAT.</summary>
    public double[] ImportNp { get; } = new double[12];

    /// <summary>Σ import × monthly mean np – exchange billing by a standard profile (no smart meter).</summary>
    public double[] ImportNpFlat { get; } = new double[12];

    public double[] Import { get; } = new double[12];
    public double[] Export { get; } = new double[12];

    /// <summary>Σ export × np per month, EUR without VAT.</summary>
    public double[] ExportNp { get; } = new double[12];

    public double TotalImport => Import.Sum();
    public double TotalExport => Export.Sum();

    public double ZoneKwh(int zones, int month, TariffZone zone) => _import[ZoningIndex(zones)][month, (int)zone];

    public double ZoneKwhYear(int zones, TariffZone zone) => Enumerable.Range(0, 12).Sum(m => ZoneKwh(zones, m, zone));

    private static int ZoningIndex(int zones) => zones switch { 2 => 1, 4 => 2, _ => 0 };

    public static FlowAggregates From(double[] import, double[]? export, double[] np)
    {
        var a = new FlowAggregates();
        var npMonthSum = new double[12];
        var npMonthCount = new int[12];
        for (var i = 0; i < TariffCalendar.Hours; i++)
        {
            npMonthSum[TariffCalendar.Month[i]] += np[i];
            npMonthCount[TariffCalendar.Month[i]]++;
        }

        for (var i = 0; i < TariffCalendar.Hours; i++)
        {
            var m = TariffCalendar.Month[i];
            var kwh = import[i];
            a.Import[m] += kwh;
            a.ImportNp[m] += kwh * np[i];
            a._import[0][m, (int)TariffZone.All] += kwh;
            a._import[1][m, (int)TariffCalendar.Zone2[i]] += kwh;
            a._import[2][m, (int)TariffCalendar.Zone4[i]] += kwh;

            if (export is not null)
            {
                a.Export[m] += export[i];
                a.ExportNp[m] += export[i] * np[i];
            }
        }

        for (var m = 0; m < 12; m++)
        {
            a.ImportNpFlat[m] = a.Import[m] * npMonthSum[m] / Math.Max(1, npMonthCount[m]);
        }

        return a;
    }
}

/// <summary>One ESO plan + zone count + supplier plan.</summary>
public sealed record PlanCombo(EsoPlan Eso, int Zones, SupplierPlan Supplier)
{
    public string Id => $"{Eso.Id}-{Zones}-{Supplier.Name}";

    public string EsoLabel => $"ESO {Eso.Id}, {ZonesLabel(Zones)}";

    public static string ZonesLabel(int zones) => zones switch { 1 => "1 zona", _ => $"{zones} zonos" };
}

/// <summary>How a prosumer settles: two-way metering (with ESO mode 1–4) or net billing.</summary>
public sealed record ProsumerSettlement(ProsumerScheme Scheme, int EsoMode, double LggKw)
{
    public string Label => Scheme == ProsumerScheme.NetMetering
        ? $"Dvipusė apskaita, {EsoMode} būdas"
        : "Grynasis atsiskaitymas";

    public static string ModeLabel(int mode) => mode switch
    {
        1 => "1 būdas · už atgautą energiją",
        2 => "2 būdas · už leistiną galią",
        3 => "3 būdas · procentais",
        4 => "4 būdas · pagal persiuntimo tarifą",
        _ => $"{mode} būdas"
    };
}

/// <summary>One line of the ESO part (a zone).</summary>
public sealed record BillLine(string Label, double Kwh, double Price, double Eur);

/// <summary>Yearly bill of one combination, split like the documentation's test bill.</summary>
public sealed class BillBreakdown
{
    public required PlanCombo Combo { get; init; }
    public ProsumerSettlement? Settlement { get; init; }

    /// <summary>ESO part per zone (kWh, price, EUR) – for prosumers only the deficit is listed.</summary>
    public List<BillLine> EsoLines { get; } = new();

    public double EsoFixed { get; set; }

    /// <summary>Supplier energy (fixed zone prices or (np + margin) × VAT).</summary>
    public double Energy { get; set; }

    public double SupplierFee { get; set; }
    public double ViapDed { get; set; }

    /// <summary>ESO fee for energy taken back from the bank (modes 1, 2, 4).</summary>
    public double ProsumerFee { get; set; }

    /// <summary>Net billing: wallet credit actually used to pay the bill (negative = saving).</summary>
    public double WalletUsed { get; set; }

    public double TakenBackKwh { get; set; }
    public double BankLeftoverKwh { get; set; }
    public double WalletLeftover { get; set; }

    public double[] PerMonth { get; } = new double[12];

    public double EsoTotal => EsoLines.Sum(l => l.Eur) + EsoFixed + ProsumerFee;

    public double Total => PerMonth.Sum();
}

/// <summary>
/// The pricing rules from the project documentation:
/// <c>kaina(t) = energija(t) + ESO_tarifas(zona(t)) + VIAP + DED</c>,
/// <c>sąskaita = Σ kwh[t] × kaina(t) + pastovūs mokesčiai</c>, plus prosumer settlement
/// (two-way metering with a monthly kWh bank and four ESO modes, or net billing with a money wallet).
/// </summary>
public sealed class BillEngine
{
    private readonly TariffCatalog _catalog;

    public BillEngine(TariffCatalog catalog)
    {
        _catalog = catalog;
    }

    public TariffCatalog Catalog => _catalog;

    /// <summary>Month order of the accumulation period (starts 1 April).</summary>
    private static readonly int[] PeriodMonths = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2 };

    /// <summary>
    /// Every allowed combination: supplier zones must match the ESO zones (exchange plans fit any),
    /// 4 zones need a smart meter, public supply only for its current customers, and long contracts
    /// can be excluded for people who move soon. Guaranteed supply is never offered.
    /// </summary>
    public IEnumerable<PlanCombo> Combos(bool smartMeter, bool includePublic, int maxDurationMonths = int.MaxValue)
    {
        foreach (var eso in _catalog.Eso.Plans)
        {
            foreach (var zones in eso.Zones.Keys.OrderBy(z => z))
            {
                if (zones == 4 && !smartMeter)
                {
                    continue;
                }

                foreach (var s in _catalog.SupplierPlans)
                {
                    if (s.Kind == SupplierKind.Public && !includePublic) continue;
                    if (s.DurationMonths > maxDurationMonths) continue;
                    if (s.Kind != SupplierKind.Exchange && s.Zones != zones) continue;

                    yield return new PlanCombo(eso, zones, s);
                }
            }
        }
    }

    /// <summary>Prices one combination for one year of flows.</summary>
    /// <param name="npMultiplier">Spot price scenario (1 = the price file, &gt;1 expensive year).</param>
    /// <param name="flatNp">Exchange billed by a standard profile (no smart meter).</param>
    public BillBreakdown Price(FlowAggregates a, PlanCombo c, double lgKw, double npMultiplier = 1,
        ProsumerSettlement? settlement = null, bool flatNp = false)
    {
        var bill = new BillBreakdown { Combo = c, Settlement = settlement };
        var zones = TariffCalendar.ZoneList(c.Zones);
        var esoPrices = c.Eso.Zones[c.Zones];
        var addEso = !c.Supplier.IncludesEso;
        var viapDed = _catalog.Eso.Viap + _catalog.Eso.Ded;
        var rules = _catalog.Prosumer;

        var esoKwh = new double[zones.Length];
        var esoEur = new double[zones.Length];

        // Energy-related part of one month for the share of import that is paid at full price.
        (double Eso, double Energy) MonthEnergy(int m, double paidShare)
        {
            double eso = 0, energy = 0;
            for (var z = 0; z < zones.Length; z++)
            {
                var kwh = a.ZoneKwh(c.Zones, m, zones[z]) * paidShare;
                if (addEso)
                {
                    var e = kwh * esoPrices[zones[z]];
                    eso += e;
                    esoKwh[z] += kwh;
                    esoEur[z] += e;
                }

                if (c.Supplier.Kind != SupplierKind.Exchange)
                {
                    energy += kwh * c.Supplier.PriceWithVat(zones[z]);
                }
            }

            if (c.Supplier.Kind == SupplierKind.Exchange)
            {
                var np = (flatNp ? a.ImportNpFlat[m] : a.ImportNp[m]) * npMultiplier;
                energy += (np + c.Supplier.Margin * a.Import[m]) * paidShare * TariffCatalog.Vat;
            }

            return (eso, energy);
        }

        var bank = 0.0;
        var wallet = 0.0;
        foreach (var m in PeriodMonths)
        {
            var fixedFees = (addEso ? c.Eso.MonthlyFee(lgKw) : 0) + c.Supplier.MonthlyFee;
            bill.EsoFixed += addEso ? c.Eso.MonthlyFee(lgKw) : 0;
            bill.SupplierFee += c.Supplier.MonthlyFee;

            var viap = a.Import[m] * viapDed;
            bill.ViapDed += viap;

            double month;
            if (settlement is { Scheme: ProsumerScheme.NetMetering } nm)
            {
                // Two-way metering: export goes to a kWh bank, taken-back kWh are not paid to the supplier.
                bank += a.Export[m] * (nm.EsoMode == 3 ? rules.Mode3BankShare : 1);
                var takenBack = Math.Min(a.Import[m], bank);
                bank -= takenBack;
                bill.TakenBackKwh += takenBack;
                var covered = a.Import[m] > 0 ? takenBack / a.Import[m] : 0;

                var (eso, energy) = MonthEnergy(m, 1 - covered);
                var fee = nm.EsoMode switch
                {
                    1 => takenBack * rules.Mode1PerKwh,
                    2 => nm.LggKw * rules.Mode2PerLggKwMonth,
                    4 => EsoForShare(m, covered),
                    _ => 0
                };
                bill.ProsumerFee += fee;
                bill.Energy += energy;
                month = eso + energy + fee + viap + fixedFees;
            }
            else if (settlement is { Scheme: ProsumerScheme.NetBilling })
            {
                // Net billing: export earns money (np − fee) that pays the energy bill.
                wallet += a.ExportNp[m] * npMultiplier - rules.NetBillingFee * a.Export[m];
                var (eso, energy) = MonthEnergy(m, 1);
                var due = eso + energy + viap;
                var used = Math.Clamp(wallet, 0, Math.Max(0, due));
                wallet -= used;
                bill.WalletUsed -= used;
                bill.Energy += energy;
                month = due - used + fixedFees;
            }
            else
            {
                var (eso, energy) = MonthEnergy(m, 1);
                bill.Energy += energy;
                month = eso + energy + viap + fixedFees;
            }

            bill.PerMonth[m] = month;
        }

        bill.BankLeftoverKwh = bank;
        bill.WalletLeftover = Math.Max(0, wallet);

        for (var z = 0; z < zones.Length; z++)
        {
            bill.EsoLines.Add(new BillLine($"ESO {TariffCalendar.Label(zones[z]).ToLowerInvariant()}", esoKwh[z], esoPrices[zones[z]], esoEur[z]));
        }

        return bill;

        // Mode 4: taken-back kWh pay the ESO tariff of their zone (same zone split as the import).
        double EsoForShare(int m, double share)
        {
            var eur = 0.0;
            foreach (var z in zones)
            {
                eur += a.ZoneKwh(c.Zones, m, z) * share * esoPrices[z];
            }

            return eur;
        }
    }

    /// <summary>
    /// Marginal price of every hour for a combination (ESO zone price + supplier energy), EUR/kWh with VAT.
    /// Used by the battery arbitrage.
    /// </summary>
    public double[] HourlyPrice(PlanCombo c)
    {
        var zoneOf = TariffCalendar.ZonesFor(c.Zones);
        var esoPrices = c.Eso.Zones[c.Zones];
        var price = new double[TariffCalendar.Hours];
        for (var i = 0; i < price.Length; i++)
        {
            var zone = zoneOf[i];
            var eso = c.Supplier.IncludesEso ? 0 : esoPrices[zone];
            var energy = c.Supplier.Kind == SupplierKind.Exchange
                ? (_catalog.Np[i] + c.Supplier.Margin) * TariffCatalog.Vat
                : c.Supplier.PriceWithVat(zone);
            price[i] = eso + energy;
        }

        return price;
    }

    /// <summary>
    /// Personal weighted spot price <c>Σ(kwh × np) / Σ kwh</c>, EUR/kWh without VAT – the main
    /// indicator of whether an exchange plan suits this household.
    /// </summary>
    public static double PersonalWeightedPrice(FlowAggregates a) =>
        a.TotalImport > 0 ? a.ImportNp.Sum() / a.TotalImport : 0;
}
