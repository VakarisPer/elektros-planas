using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using vamos.Data;
using vamos.Models;
using Q = vamos.Data.Questionnaire;

namespace vamos.Services;

/// <summary>
/// Deterministic cost model. Turns the survey answers into a <see cref="HouseholdProfile"/> and
/// estimates the yearly energy cost of every supplier plan. The AI advisor never invents numbers –
/// it only explains what this class returns.
/// </summary>
public sealed class PlanCalculator
{
    private static readonly CultureInfo Lt = CultureInfo.GetCultureInfo("lt-LT");

    private readonly TariffOptions _tariffs;
    private readonly SolarOptions _solar;

    public PlanCalculator(IOptions<TariffOptions> tariffs, IOptions<SolarOptions> solar)
    {
        _tariffs = tariffs.Value;
        _solar = solar.Value;
    }

    public TariffOptions Tariffs => _tariffs;

    // ── Profile ─────────────────────────────────────────────────────────

    public HouseholdProfile BuildProfile(QuestionnaireState state)
    {
        bool Has(int id, string option) => state.GetAnswer(id).SelectedOptions.Contains(option);
        bool HasAnswer(int id) => state.GetAnswer(id).SelectedOptions.Count > 0;
        string? First(int id) => state.GetAnswer(id).SelectedOptions.FirstOrDefault();

        var p = new HouseholdProfile();

        // EV yearly kWh (used for scale and for the night share).
        var evKwh = 0.0;
        if (Has(7, Q.EvChargeAtHome))
        {
            evKwh = First(25) switch
            {
                "Iki 500 km" => 650,
                "Virš 1500 km" => 4300,
                _ => 2200
            };
        }

        // ── Scale ──
        var estimate = EstimateAnnualKwh(state, evKwh);
        if (state.Analysis is { Success: true, MonthlyAverageKwh: { } monthly })
        {
            p.AnnualKwh = monthly * 12;
            p.ConsumptionFromFile = true;
            if (state.Analysis.ExportedMonthlyKwh is > 1)
            {
                p.Notes.Add("Faile – tik iš tinklo pirkta energija; tiesiogiai suvartota saulės energija nematoma.");
            }
        }
        else
        {
            p.AnnualKwh = First(3) switch
            {
                Q.MonthlyLow => 1300,
                Q.MonthlyMid => 2700,
                Q.MonthlyHigh => Math.Max(4200, estimate),
                _ => estimate
            };

            if (!HasAnswer(3) || First(3) == "Nežinau")
            {
                p.Notes.Add("Suvartojimas įvertintas pagal būstą ir įrangą – tikslumui įkelkite ESO duomenis.");
            }
        }

        // ── When energy is used ──
        p.NightShare = state.Analysis is { Success: true, NightShare: { } night } ? night : 0.22;
        p.EveningShare = 0.40;

        switch (First(15))
        {
            case "Vakare (17–22 val.)": p.EveningShare = 0.50; break;
            case "Dieną (9–17 val.)": p.EveningShare = 0.30; break;
            case "Naktį (22–6 val.)": p.NightShare += 0.12; break;
        }

        switch (First(14))
        {
            case "Namuose nieko nėra 8–17 val.": p.EveningShare += 0.05; break;
            case "Namuose būna beveik visą dieną": p.EveningShare -= 0.05; break;
        }

        var evShare = p.AnnualKwh > 0 ? Math.Min(0.6, evKwh / p.AnnualKwh) : 0;
        if (Has(18, Q.EvNightOnly))
        {
            p.NightShare += evShare * 0.8;
        }

        p.NightShare = Math.Clamp(p.NightShare, 0.05, 0.7);
        p.EveningShare = Math.Clamp(p.EveningShare, 0.1, 0.95 - p.NightShare);

        // ── Flexibility ──
        var shift = 0.0;
        if (Has(16, Q.RoutineYes)) shift += 0.15;
        else if (Has(16, Q.RoutineMaybe)) shift += 0.07;
        if (Has(12, Q.TimersMost)) shift += 0.06;
        else if (Has(12, Q.TimersSome)) shift += 0.03;
        if (HasAnswer(13) && !Has(13, Q.NoSmartHome)) shift += 0.06;
        if (Has(23, "Taip, jau valdau automatiškai")) shift += 0.08;
        if (Has(18, Q.EvCanDelay)) shift += evShare * 0.8;
        if (Has(5, Q.ElectricBoiler) && !Has(16, Q.NoRoutineChange)) shift += 0.03;

        p.HasBattery = Has(19, Q.BatteryYes);
        if (p.HasBattery)
        {
            shift += Has(28, Q.BatteryPriceControlled) ? 0.15 : 0.06;
        }

        p.HasSmartMeter = Has(20, Q.SmartMeterYes) || state.Analysis is { Success: true, NightShare: not null };
        if (!p.HasSmartMeter && HasAnswer(20))
        {
            p.Notes.Add("Be ESO išmaniojo skaitiklio biržos planas skaičiuojamas pagal standartinį profilį – perkėlimo nauda neįskaičiuota.");
        }

        p.ShiftPotential = Math.Clamp(shift, 0, 0.45);

        // ── Own generation ──
        p.IsHouse = First(1)?.StartsWith("Namas", StringComparison.Ordinal) == true;
        p.EvKwh = evKwh;
        p.EvNightOnly = Has(18, Q.EvNightOnly);
        p.ElectricHeating = Has(4, Q.HeatPump) || Has(4, Q.ElectricHeating);
        p.PeakHours = First(15) switch
        {
            "Ryte (6–9 val.)" => "6-9",
            "Dieną (9–17 val.)" => "9-17",
            "Naktį (22–6 val.)" => "22-6",
            "Vakare (17–22 val.)" => "17-22",
            _ => First(14) == "Namuose būna beveik visą dieną" ? "9-12,17-22" : "7-9,17-22"
        };

        // An ESO file of a producer contains grid import (P+) and export (P-) separately.
        var analysis = state.Analysis is { Success: true } ok ? ok : null;
        var exportedMonthly = analysis?.ExportedMonthlyKwh is > 1 ? analysis.ExportedMonthlyKwh.Value : 0;

        p.Solar = Has(8, Q.OwnSolar) || exportedMonthly > 0 ? SolarStatus.Own
            : Has(8, Q.RemoteSolar) ? SolarStatus.Remote
            : Has(8, Q.SolarInterested) ? SolarStatus.Interested
            : SolarStatus.None;
        p.HasSolar = p.Solar is SolarStatus.Own or SolarStatus.Remote;
        p.ExistingKwp = First(26) switch
        {
            "Iki 5 kW" => 4.0,
            "5–10 kW" => 7.5,
            "Virš 10 kW" => 12.0,
            _ => p.HasSolar ? null : 0
        };
        p.Battery = First(19) switch
        {
            Q.BatteryYes => BatteryStatus.Has,
            Q.BatteryConsidering => BatteryStatus.Considering,
            "Ne" => BatteryStatus.No,
            _ => BatteryStatus.Unknown
        };
        p.BatteryPriceControlled = Has(28, Q.BatteryPriceControlled);
        p.Scheme = First(27) switch
        {
            "Perteklių parduodu" => ProsumerScheme.SellSurplus,
            "Kaupimas ESO tinkle (grąžinu žiemą)" => ProsumerScheme.NetMetering,
            _ => null
        };

        p.PurchasedKwh = p.AnnualKwh;
        if (p.HasSolar)
        {
            var scheme = p.Scheme ?? _solar.DefaultScheme;
            double import, export;

            if (exportedMonthly > 0)
            {
                // The file already shows grid import – do not subtract generation twice.
                import = p.AnnualKwh;
                export = exportedMonthly * 12;
            }
            else
            {
                // No measured data: simulate the plant against an estimated hourly load.
                var kwp = p.ExistingKwp ?? 6.0;
                var load = LoadProfileBuilder.Synthetic(p.AnnualKwh, LoadProfileBuilder.ParseHours(p.PeakHours),
                    winterBoost: p.ElectricHeating ? 0.5 : 0.15);
                var sim = SolarSimulator.Simulate(load, kwp, p.Battery == BatteryStatus.Has ? _solar.AssumedExistingBatteryKwh : 0,
                    new SimOptions(_solar.Yield, _solar.BatteryEff, _solar.Dod, _solar.CRate) { RemoteOnly = p.Solar == SolarStatus.Remote });
                import = sim.Import;
                export = sim.Export;
            }

            // Net metering: energy taken back from the grid is not bought from the supplier.
            var takenBack = scheme == ProsumerScheme.NetMetering ? Math.Min(import, export) : 0;
            p.PurchasedKwh = Math.Max(0, import - takenBack);
            p.WinterWeight = scheme == ProsumerScheme.NetMetering ? 1.25 : 1.1;

            p.Notes.Add(exportedMonthly > 0
                ? $"Pagal ESO duomenis per metus iš tinklo paimate ~{import.ToString("0", Lt)} kWh, atiduodate ~{export.ToString("0", Lt)} kWh."
                : $"Saulės elektrinė pagal modelį pagamina ~{(export + Math.Max(0, p.AnnualKwh - import)).ToString("0", Lt)} kWh per metus.");
            if (takenBack > 0)
            {
                p.Notes.Add($"Kaupimas tinkle: ~{takenBack.ToString("0", Lt)} kWh atsiimate iš tinklo (mokate tik ESO mokestį), iš tiekėjo perkate ~{p.PurchasedKwh.ToString("0", Lt)} kWh.");
            }
        }

        if (Has(4, Q.HeatPump) || Has(4, Q.ElectricHeating))
        {
            p.WinterWeight += 0.15;
        }

        // ── Risk attitude ──
        p.RiskAversion = First(17) switch
        {
            Q.PlanFixed => 0.9,
            Q.PlanMiddle => 0.5,
            Q.PlanOptimize => 0.2,
            _ => 0.6
        };

        if (Has(30, Q.DoubleBillUnacceptable)) p.RiskAversion = Math.Max(p.RiskAversion, 0.9);
        else if (Has(30, Q.DoubleBillOk)) p.RiskAversion = Math.Min(p.RiskAversion, 0.25);
        else if (HasAnswer(30)) p.RiskAversion = (p.RiskAversion + 0.5) / 2;

        if (Has(31, "Mažiau nei metus"))
        {
            p.Notes.Add("Greitai kraustotės – venkite ilgų fiksuotų sutarčių su nutraukimo mokesčiu.");
        }

        return p;
    }

    private static double EstimateAnnualKwh(QuestionnaireState state, double evKwh)
    {
        bool Has(int id, string option) => state.GetAnswer(id).SelectedOptions.Contains(option);
        var first = state.GetAnswer(1).SelectedOptions.FirstOrDefault();
        var isHouse = first?.StartsWith("Namas", StringComparison.Ordinal) == true;

        var kwh = first switch
        {
            "Butas iki 50 m²" => 1300,
            "Butas 50–80 m²" => 1800,
            "Butas virš 80 m²" => 2300,
            "Namas iki 100 m²" => 3000,
            "Namas 100–200 m²" => 4000,
            "Namas virš 200 m²" => 5500,
            _ => 2000.0
        };

        kwh += state.GetAnswer(2).SelectedOptions.FirstOrDefault() switch
        {
            "1" => -300,
            "3" => 400,
            "4" => 800,
            "5 ar daugiau" => 1200,
            _ => 0
        };

        if (Has(4, Q.HeatPump)) kwh += isHouse ? 4000 : 1800;
        if (Has(4, Q.ElectricHeating)) kwh += isHouse ? 8000 : 3500;
        if (Has(5, Q.ElectricBoiler)) kwh += 1500;
        if (Has(9, "Elektrinė pirtis")) kwh += 800;
        if (Has(9, "Šildomas lauko baseinas")) kwh += 1500;
        if (Has(9, "Sūkurinė vonia")) kwh += 500;
        if (Has(10, "Namų serveris / NAS")) kwh += 400;
        if (Has(10, Q.CryptoMining)) kwh += 8000;
        if (Has(11, "Taip, kasdien kelias valandas")) kwh += 500;
        if (Has(6, "Kondicionierius")) kwh += 300;

        return kwh + evKwh;
    }

    // ── Plans ───────────────────────────────────────────────────────────

    public IReadOnlyList<PlanEstimate> Estimate(HouseholdProfile p)
    {
        var results = new List<PlanEstimate>();
        var kwh = p.PurchasedKwh;

        // Without a smart meter the exchange plan is billed by a standard profile – no shifting.
        var exchangeShift = p.HasSmartMeter ? p.ShiftPotential : 0;
        var (nEx, dEx, eEx) = p.HasSmartMeter ? Shifted(p, exchangeShift) : (0.25, 0.35, 0.40);
        var spotShape = nEx * _tariffs.NightFactor + dEx * _tariffs.DayFactor + eEx * _tariffs.EveningFactor;
        var spot = _tariffs.AverageSpotPrice * spotShape * p.WinterWeight;

        // Two-zone: night (23–7) and the whole weekend are billed at the night price.
        var (nTz, _, _) = Shifted(p, p.ShiftPotential * 0.8);
        var nightTz = nTz + (1 - nTz) * 0.28;
        var (nTzBest, _, _) = Shifted(p, Math.Min(0.45, p.ShiftPotential * 0.8 + 0.1));
        var nightTzBest = nTzBest + (1 - nTzBest) * 0.28;

        foreach (var s in _tariffs.Suppliers)
        {
            var fee = s.MonthlyFee * 12;

            var fixedCost = kwh * s.FixedPrice + fee;
            results.Add(Make(PlanType.Fixed, s.Name, s.FixedPlanName, fixedCost, fixedCost, fixedCost, kwh, p));

            var exCost = kwh * (spot + s.ExchangeMargin) + fee;
            var exWorst = kwh * (spot * _tariffs.SpotStressFactor + s.ExchangeMargin) + fee;
            var exBest = kwh * (spot * _tariffs.SpotCalmFactor + s.ExchangeMargin) + fee;
            results.Add(Make(PlanType.Exchange, s.Name, s.ExchangePlanName, exCost, exWorst, exBest, kwh, p));

            var tzCost = kwh * (nightTz * s.TwoZoneNightPrice + (1 - nightTz) * s.TwoZoneDayPrice) + fee;
            var tzBest = kwh * (nightTzBest * s.TwoZoneNightPrice + (1 - nightTzBest) * s.TwoZoneDayPrice) + fee;
            // Forgetting to shift (habits slip back) is the realistic downside of two-zone plans.
            var (nNoShift, _, _) = Shifted(p, 0);
            var nightNoShift = nNoShift + (1 - nNoShift) * 0.28;
            var tzWorst = kwh * (nightNoShift * s.TwoZoneNightPrice + (1 - nightNoShift) * s.TwoZoneDayPrice) + fee;
            results.Add(Make(PlanType.TwoZone, s.Name, s.TwoZonePlanName, tzCost, Math.Max(tzCost, tzWorst), Math.Min(tzCost, tzBest), kwh, p));
        }

        return results.OrderBy(r => r.Score).ToList();
    }

    private static (double Night, double Day, double Evening) Shifted(HouseholdProfile p, double shift)
    {
        var evening = p.EveningShare;
        var day = p.DayShare;
        var fromEvening = Math.Min(evening, shift * 0.7);
        var fromDay = Math.Min(day, shift - fromEvening);
        return (p.NightShare + fromEvening + fromDay, day - fromDay, evening - fromEvening);
    }

    private static PlanEstimate Make(PlanType type, string supplier, string name, double cost, double worst, double best, double kwh, HouseholdProfile p)
    {
        var spread = cost > 0 ? (worst - cost) / cost : 0;
        return new PlanEstimate
        {
            Type = type,
            Supplier = supplier,
            PlanName = name,
            YearlyCost = Math.Round(cost),
            WorstCost = Math.Round(worst),
            BestCost = Math.Round(best),
            EffectivePrice = kwh > 0 ? cost / kwh : 0,
            Score = cost + p.RiskAversion * (worst - cost),
            RiskLevel = spread switch
            {
                > 0.3 => "Aukšta",
                > 0.08 => "Vidutinė",
                _ => "Žema"
            }
        };
    }

    // ── Hourly (12×24) model, used for the solar comparison ─────────────

    /// <summary>
    /// Supplier energy cost of every plan for an hourly import profile (kWh per month × 24 + hour cell
    /// over a year). Under net metering the energy taken back from the grid is not bought.
    /// Same output shape as <see cref="Estimate"/> so both can be shown with the same cards.
    /// </summary>
    /// <param name="fullBill">
    /// Adds ESO distribution, the net-metering fee and surplus income, so a prosumer's bill is not
    /// misleadingly close to zero (under net metering almost no energy is bought from the supplier).
    /// </param>
    public IReadOnlyList<PlanEstimate> EstimateFromMatrix(double[] import288, double[] export288, ProsumerScheme scheme, HouseholdProfile p, bool fullBill = false)
    {
        var billed = BilledCells(import288, export288, scheme);
        var kwh = billed.Sum();
        var results = new List<PlanEstimate>();

        var grid = 0.0;
        if (fullBill)
        {
            var takenBack = import288.Sum() - kwh;
            grid = kwh * _solar.DistributionPrice + takenBack * _solar.NetMeteringFee
                   - (scheme == ProsumerScheme.SellSurplus ? export288.Sum() * _solar.ExportPrice : 0);
        }

        foreach (var s in _tariffs.Suppliers)
        {
            foreach (var type in Enum.GetValues<PlanType>())
            {
                var cost = EnergyCost(billed, type, s, p, 1.0) + grid;
                var worst = type switch
                {
                    PlanType.Exchange => EnergyCost(billed, type, s, p, _tariffs.SpotStressFactor) + grid,
                    PlanType.TwoZone => EnergyCost(billed, type, s, p, 1.0, shiftOverride: 0) + grid,
                    _ => cost
                };
                var best = type == PlanType.Exchange ? EnergyCost(billed, type, s, p, _tariffs.SpotCalmFactor) + grid : cost;
                var name = type switch
                {
                    PlanType.Fixed => s.FixedPlanName,
                    PlanType.Exchange => s.ExchangePlanName,
                    _ => s.TwoZonePlanName
                };
                // With the full bill, the per-kWh price refers to all energy taken from the grid.
                results.Add(Make(type, s.Name, name, cost, Math.Max(cost, worst), Math.Min(cost, best), fullBill ? import288.Sum() : kwh, p));
            }
        }

        return results.OrderBy(r => r.Score).ToList();
    }

    /// <summary>
    /// Full yearly electricity bill for one simulated configuration: supplier energy + ESO distribution
    /// on bought energy + net-metering fee on energy taken back − income for sold surplus.
    /// Distribution is added only here so plan cards stay "energy only".
    /// </summary>
    public double TotalBill(SimResult r, PlanType type, SupplierTariff s, ProsumerScheme scheme, HouseholdProfile p) =>
        MonthlyBill(r.Import288, r.Export288, type, s, scheme, p).Sum();

    /// <summary><see cref="TotalBill"/> split by month (net metering is settled over the year).</summary>
    public double[] MonthlyBill(double[] import288, double[] export288, PlanType type, SupplierTariff s, ProsumerScheme scheme, HouseholdProfile p)
    {
        var importTotal = import288.Sum();
        var exportTotal = export288.Sum();
        var takenBack = scheme == ProsumerScheme.NetMetering ? Math.Min(importTotal, exportTotal) : 0;
        var billedShare = importTotal > 0 ? 1 - takenBack / importTotal : 1;
        var billed = import288.Select(x => x * billedShare).ToArray();
        var shifted = Shift(billed, type, p, null);
        var spot = SpotCells(1.0);

        var months = new double[12];
        for (var c = 0; c < 288; c++)
        {
            var m = c / 24;
            months[m] += shifted[c] * (CellPrice(type, s, c, spot) + _solar.DistributionPrice)
                         + import288[c] * (1 - billedShare) * _solar.NetMeteringFee;
            if (scheme == ProsumerScheme.SellSurplus)
            {
                months[m] -= export288[c] * _solar.ExportPrice;
            }
        }

        for (var m = 0; m < 12; m++)
        {
            months[m] += s.MonthlyFee;
        }

        return months;
    }

    private static double[] BilledCells(double[] import288, double[] export288, ProsumerScheme scheme)
    {
        var importTotal = import288.Sum();
        var takenBack = scheme == ProsumerScheme.NetMetering ? Math.Min(importTotal, export288.Sum()) : 0;
        var share = importTotal > 0 ? 1 - takenBack / importTotal : 1;
        return import288.Select(x => x * share).ToArray();
    }

    private double EnergyCost(double[] billed, PlanType type, SupplierTariff s, HouseholdProfile p, double spotMultiplier, double? shiftOverride = null)
    {
        var cells = Shift(billed, type, p, shiftOverride);
        var spot = SpotCells(spotMultiplier);
        var cost = s.MonthlyFee * 12;
        for (var c = 0; c < 288; c++)
        {
            cost += cells[c] * CellPrice(type, s, c, spot);
        }

        return cost;
    }

    /// <summary>Moves the flexible share of 7–23 h consumption into 0–7 h of the same month.</summary>
    private static double[] Shift(double[] cells, PlanType type, HouseholdProfile p, double? shiftOverride)
    {
        var shift = shiftOverride ?? type switch
        {
            PlanType.Exchange when p.HasSmartMeter => p.ShiftPotential,
            PlanType.TwoZone => p.ShiftPotential * 0.8,
            _ => 0
        };

        var result = (double[])cells.Clone();
        if (shift <= 0)
        {
            return result;
        }

        for (var m = 0; m < 12; m++)
        {
            var moved = 0.0;
            for (var h = 7; h < 23; h++)
            {
                var amount = result[m * 24 + h] * shift;
                result[m * 24 + h] -= amount;
                moved += amount;
            }

            for (var h = 0; h < 7; h++)
            {
                result[m * 24 + h] += moved / 7;
            }
        }

        return result;
    }

    private double[] SpotCells(double multiplier)
    {
        var spot = new double[288];
        for (var c = 0; c < 288; c++)
        {
            var hour = c % 24;
            var hourFactor = hour is >= 23 or < 7 ? _tariffs.NightFactor
                : hour >= 17 ? _tariffs.EveningFactor
                : _tariffs.DayFactor;
            var monthFactor = _tariffs.MonthlySpotFactors.Length == 12 ? _tariffs.MonthlySpotFactors[c / 24] : 1;
            spot[c] = _tariffs.AverageSpotPrice * multiplier * monthFactor * hourFactor;
        }

        return spot;
    }

    private static double CellPrice(PlanType type, SupplierTariff s, int cell, double[] spot)
    {
        var hour = cell % 24;
        return type switch
        {
            PlanType.Fixed => s.FixedPrice,
            PlanType.Exchange => spot[cell] + s.ExchangeMargin,
            // Night 23–7 is cheap; on weekends (2/7 of days) the whole day is cheap.
            _ => hour is >= 23 or < 7
                ? s.TwoZoneNightPrice
                : 2.0 / 7 * s.TwoZoneNightPrice + 5.0 / 7 * s.TwoZoneDayPrice
        };
    }

    /// <summary>Cheapest plan of every type (one per type), best score first.</summary>
    public static IReadOnlyList<PlanEstimate> BestPerType(IEnumerable<PlanEstimate> estimates) =>
        estimates.GroupBy(e => e.Type)
            .Select(g => g.OrderBy(e => e.Score).First())
            .OrderBy(e => e.Score)
            .ToList();

    // ── Text for the advisor and the rule-based fallback ────────────────

    public string Describe(HouseholdProfile p, IReadOnlyList<PlanEstimate> estimates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROFILIS:");
        sb.AppendLine($"- Metinis suvartojimas: {p.AnnualKwh:0} kWh{(p.ConsumptionFromFile ? " (iš įkelto failo)" : " (įvertinta)")}; perkama iš tinklo: {p.PurchasedKwh:0} kWh");
        sb.AppendLine($"- Vartojimas: naktį {p.NightShare:P0}, dieną {p.DayShare:P0}, vakare {p.EveningShare:P0}");
        sb.AppendLine($"- Perkeliama dalis į pigias valandas: {p.ShiftPotential:P0}; išmanusis skaitiklis: {(p.HasSmartMeter ? "yra" : "nėra/nežinoma")}");
        sb.AppendLine($"- Rizikos vengimas (0–1): {p.RiskAversion:0.00}; sava gamyba: {(p.HasSolar ? "taip" : "ne")}; kaupiklis: {(p.HasBattery ? "taip" : "ne")}");
        foreach (var note in p.Notes)
        {
            sb.AppendLine($"- Pastaba: {note}");
        }

        sb.AppendLine();
        sb.AppendLine("PLANAI (energijos dalis be ESO skirstymo, EUR/metus; bazinis / brangus metai / pigūs metai):");
        foreach (var e in estimates)
        {
            sb.AppendLine($"- {e.PlanName} [{e.Type}, {e.Supplier}]: {e.YearlyCost:0} / {e.WorstCost:0} / {e.BestCost:0}; rizika {e.RiskLevel}; balas {e.Score:0}");
        }

        var best = BestPerType(estimates);
        sb.AppendLine();
        sb.AppendLine($"SKAIČIUOKLĖS SIŪLYMAS (pagal kainą ir rizikos toleranciją): {best[0].PlanName} ({best[0].Type}).");
        return sb.ToString();
    }

    /// <summary>Recommendation without the language model (no API key or the model failed).</summary>
    public Recommendation RuleBasedRecommendation(HouseholdProfile p, IReadOnlyList<PlanEstimate> estimates)
    {
        var best = BestPerType(estimates);
        var top = best[0];
        var fixedPlan = best.First(e => e.Type == PlanType.Fixed);

        var sb = new StringBuilder();
        sb.AppendLine($"**Rekomendacija:** {Recommendation.PlanTypeLabel(top.Type).ToLower(Lt)} – {top.PlanName}.");
        sb.AppendLine();
        sb.AppendLine("**Kodėl jums:**");
        sb.AppendLine($"- Perkate apie {p.PurchasedKwh.ToString("0", Lt)} kWh per metus.");
        if (p.ShiftPotential >= 0.1)
        {
            sb.AppendLine($"- Galite perkelti apie {(p.ShiftPotential * 100).ToString("0", Lt)} % vartojimo į pigias valandas.");
        }
        sb.AppendLine(p.RiskAversion >= 0.7
            ? "- Jums svarbi stabili, nuspėjama sąskaita."
            : "- Esate pasirengę priimti tam tikrus kainų svyravimus dėl mažesnės vidutinės kainos.");
        sb.AppendLine();

        if (top.Type != PlanType.Fixed)
        {
            var diff = fixedPlan.YearlyCost - top.YearlyCost;
            sb.AppendLine($"**Kiek kainuotų:** ~{top.YearlyCost.ToString("0", Lt)} € per metus (fiksuotas – ~{fixedPlan.YearlyCost.ToString("0", Lt)} €, skirtumas ~{diff.ToString("0", Lt)} €).");
            sb.AppendLine();
            sb.AppendLine($"**Rizikos:** brangiais metais gali kainuoti iki ~{top.WorstCost.ToString("0", Lt)} €.");
        }
        else
        {
            sb.AppendLine($"**Kiek kainuotų:** ~{top.YearlyCost.ToString("0", Lt)} € per metus, be netikėtumų.");
        }

        sb.AppendLine();
        sb.AppendLine("*Skaičiai orientaciniai – prieš sudarydami sutartį pasitikrinkite aktualias tiekėjo sąlygas.*");

        return new Recommendation
        {
            PlanType = top.Type,
            Supplier = top.Supplier,
            SummaryMarkdown = sb.ToString(),
            Confidence = p.ConsumptionFromFile ? "Aukštas" : "Vidutinis",
            ByAi = false
        };
    }
}
