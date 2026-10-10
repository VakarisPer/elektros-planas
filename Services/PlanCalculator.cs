using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using vamos.Data;
using vamos.Models;
using Q = vamos.Data.Questionnaire;

namespace vamos.Services;

/// <summary>
/// Deterministic cost model. Turns the survey answers into a <see cref="HouseholdProfile"/> with an
/// hourly import / export series and prices every allowed combination of ESO plan × zones × supplier
/// plan with <see cref="BillEngine"/> (full bill: ESO + energy + VIAP/DED + fixed fees ± solar).
/// The AI advisor never invents numbers – it only explains what this class returns.
/// </summary>
public sealed class PlanCalculator
{
    private static readonly CultureInfo Lt = CultureInfo.GetCultureInfo("lt-LT");

    private readonly TariffOptions _tariffs;
    private readonly SolarOptions _solar;
    private readonly BillEngine _engine;

    public PlanCalculator(IOptions<TariffOptions> tariffs, IOptions<SolarOptions> solar, BillEngine engine)
    {
        _tariffs = tariffs.Value;
        _solar = solar.Value;
        _engine = engine;
    }

    public BillEngine Engine => _engine;

    public TariffCatalog Catalog => _engine.Catalog;

    public TariffOptions Tariffs => _tariffs;

    // ── Profile ─────────────────────────────────────────────────────────

    public HouseholdProfile BuildProfile(QuestionnaireState state)
    {
        bool Has(int id, string option) => state.GetAnswer(id).SelectedOptions.Contains(option);
        bool HasAnswer(int id) => state.GetAnswer(id).SelectedOptions.Count > 0;
        string? First(int id) => state.GetAnswer(id).SelectedOptions.FirstOrDefault();

        var p = new HouseholdProfile();

        // EV yearly kWh (charged at home, mostly at night).
        var evKwh = Has(7, Q.EvChargeAtHome) ? 2200 : 0.0; // ~1 000 km per month

        // ── Scale: the ESO file, otherwise the answer to Q3, otherwise housing + people + appliances ──
        var analysis = state.Analysis is { Success: true } ok ? ok : null;
        var estimate = EstimateAnnualKwh(state, evKwh);
        if (analysis is { MonthlyAverageKwh: { } monthly })
        {
            p.AnnualKwh = monthly * 12;
            p.ConsumptionFromFile = true;
            p.ConsumptionSource = "pagal ESO duomenis";
        }
        else if (StatedAnnualKwh(state.GetAnswer(3), estimate) is { } stated)
        {
            p.AnnualKwh = stated;
            p.ConsumptionSource = "pagal jūsų nurodytą suvartojimą";
        }
        else
        {
            p.AnnualKwh = estimate;
            p.ConsumptionSource = "apytiksliai pagal būstą ir prietaisus";
            p.Notes.Add("Suvartojimas įvertintas apytiksliai pagal būstą, gyventojus ir prietaisus – tikslumui nurodykite suvartojimą arba įkelkite ESO duomenis.");
        }

        // ── Household ──
        p.IsHouse = Q.IsHouseAnswer(First(1));
        p.EvKwh = evKwh;
        p.ElectricHeating = Has(4, Q.HeatPump) || Has(4, Q.ElectricHeating);

        // ── Own generation / interest (houses only in the scheme; an ESO file with export always counts) ──
        var exportedMonthly = analysis?.ExportedMonthlyKwh is > 1 ? analysis.ExportedMonthlyKwh.Value : 0;
        p.Solar = Has(8, Q.OwnSolar) || exportedMonthly > 0 ? SolarStatus.Own
            : p.IsHouse && Has(8, Q.SolarInterested) ? SolarStatus.Interested
            : SolarStatus.None;
        p.HasSolar = p.Solar == SolarStatus.Own;

        if (p.Solar == SolarStatus.Interested)
        {
            // Below 5 kW of connectable power only a battery is possible.
            p.LowGridCapacity = Has(Q.CapacityId, Q.CapacityLow);
            p.WantsPanels = !p.LowGridCapacity && !Has(Q.InterestId, Q.WantBattery);
            p.WantsBattery = p.LowGridCapacity || !Has(Q.InterestId, Q.WantPanels);
        }

        p.ExistingKwp = First(26) switch
        {
            "Iki 5 kW" => 4.0,
            "5–10 kW" => 7.5,
            "Virš 10 kW" => 12.0,
            _ => p.HasSolar ? null : 0
        };

        var eso = state.EsoConnection is { Success: true } e ? e : null;
        // ESO knows the exact installed generation power – better than any answer bucket.
        if (eso is { InstalledGenerationKw: > 0 })
        {
            p.ExistingKwp = eso.InstalledGenerationKw;
            p.Notes.Add($"ESO duomenimis prijungta elektrinė: {eso.InstalledGenerationKw!.Value.ToString("0.##", Lt)} kW.");
        }

        p.Battery = First(19) switch
        {
            Q.BatteryYes => BatteryStatus.Has,
            "Ne" => BatteryStatus.No,
            _ => BatteryStatus.Unknown
        };
        p.HasBattery = p.Battery == BatteryStatus.Has;

        // ── Meter: a simple meter allows 1 or 2 zones; solar or a battery needs a smart one (ESO replaces it free) ──
        p.HasSmartMeter = Has(20, Q.SmartMeterYes) || analysis?.ImportMatrix is not null;
        if (!p.HasSmartMeter && p.Solar != SolarStatus.None)
        {
            p.HasSmartMeter = true;
            p.Notes.Add("Saulės elektrinei ar kaupikliui reikia išmaniojo skaitiklio – ESO jį pakeičia nemokamai; skaičiuojama su išmaniuoju.");
        }
        else if (!p.HasSmartMeter)
        {
            p.Notes.Add("Su paprastu skaitikliu galimos tik 1 arba 2 zonos; biržos planas apskaitomas pagal standartinį profilį.");
        }

        // ── Flexibility: only declared willingness to optimise, plus an EV that can wait for the night ──
        var evShare = p.AnnualKwh > 0 ? Math.Min(0.6, evKwh / p.AnnualKwh) : 0;
        var shift = First(17) switch
        {
            Q.PlanOptimize => 0.10 + evShare * 0.8,
            Q.PlanMiddle => 0.05 + evShare * 0.4,
            _ => 0
        };
        p.ShiftPotential = Math.Clamp(shift, 0, 0.45);

        // ── Permitted power ──
        p.LgFromEso = eso?.AllowedConsumptionKw is > 0;
        p.LgKw = p.LgFromEso ? eso!.AllowedConsumptionKw!.Value : p.IsHouse ? _tariffs.DefaultHouseLgKw : _tariffs.DefaultFlatLgKw;

        // ── Hourly flows ──
        BuildFlows(p, analysis);
        p.LggKw = eso?.AllowedGenerationKw is > 0 ? eso.AllowedGenerationKw!.Value : p.ExistingKwp ?? 0;
        FillShares(p);

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

        return p;
    }

    /// <summary>
    /// Consumption, import and export of every hour: from the ESO file when it has hourly data,
    /// otherwise a synthetic profile; own generation is simulated when it is not measured.
    /// </summary>
    private void BuildFlows(HouseholdProfile p, ConsumptionAnalysis? analysis)
    {
        var battery = p.Solar == SolarStatus.Own && p.Battery == BatteryStatus.Has ? _solar.AssumedExistingBatteryKwh : 0;
        var sim = new SimOptions(_solar.Yield, _solar.BatteryEff, _solar.CRate);

        if (analysis?.ImportMatrix is { Length: 576 } importMatrix)
        {
            p.FlowsFromFile = true;
            var import = LoadProfileBuilder.FromMatrix(importMatrix);
            p.Import = import;

            if (analysis.ExportMatrix is { Length: 576 } exportMatrix)
            {
                // Prosumer file: real consumption = import + self-consumed generation.
                var minKwp = LoadProfileBuilder.MinimumKwpForExport(exportMatrix, _solar.Yield);
                var kwp = Math.Max(p.ExistingKwp ?? minKwp * 1.3, minKwp);
                p.ExistingKwp = kwp;
                p.Consumption = LoadProfileBuilder.FromProsumerMatrices(importMatrix, exportMatrix, kwp, _solar.Yield);
                p.Export = LoadProfileBuilder.FromMatrix(exportMatrix);
                p.Notes.Add($"Pagal ESO duomenis per metus iš tinklo paimate ~{import.Sum().ToString("0", Lt)} kWh, atiduodate ~{p.Export.Sum().ToString("0", Lt)} kWh.");
            }
            else if (p.HasSolar)
            {
                // Import-only file of a producer: export is not measured, so it is modelled
                // (~30 % of generation is self-consumed by a plant without a battery).
                var kwp = p.ExistingKwp ?? 6;
                p.ExistingKwp = kwp;
                p.Consumption = import.Select((x, i) => x + SolarSimulator.GenShape[i] * _solar.Yield * kwp * 0.3).ToArray();
                p.Export = SolarSimulator.Simulate(p.Consumption, kwp, battery, sim).ExportSeries;
                p.Notes.Add("Faile – tik iš tinklo pirkta energija; į tinklą atiduota energija įvertinta modeliu.");
            }
            else
            {
                p.Consumption = import;
            }

            p.AnnualKwh = p.Consumption.Sum();
            p.PurchasedKwh = p.Import.Sum();
            return;
        }

        var load = SyntheticLoad(p);
        p.Consumption = load;
        if (p.HasSolar)
        {
            var kwp = p.ExistingKwp ?? 6;
            p.ExistingKwp = kwp;
            var r = SolarSimulator.Simulate(load, kwp, battery, sim);
            p.Import = r.ImportSeries;
            p.Export = r.ExportSeries;
            p.Notes.Add($"Saulės elektrinė pagal modelį pagamina ~{r.Generation.ToString("0", Lt)} kWh per metus, į tinklą atiduodama ~{r.Export.ToString("0", Lt)} kWh.");
        }
        else
        {
            p.Import = load;
        }

        p.PurchasedKwh = p.Import.Sum();
    }

    /// <summary>Synthetic hourly consumption: peak hours, winter boost with electric heating, EV at night.</summary>
    public static double[] SyntheticLoad(HouseholdProfile p)
    {
        var baseKwh = Math.Max(500, p.AnnualKwh - p.EvKwh);
        var load = LoadProfileBuilder.Synthetic(baseKwh, LoadProfileBuilder.ParseHours(p.PeakHours),
            winterBoost: p.ElectricHeating ? 0.5 : 0.15, weekendDaytime: true);

        // EV charging is assumed at night (0–6), the usual choice for home charging.
        if (p.EvKwh > 0)
        {
            var perHour = p.EvKwh / (365 * 6);
            for (var i = 0; i < load.Length; i++)
            {
                if (i % 24 < 6)
                {
                    load[i] += perHour;
                }
            }
        }

        return load;
    }

    private void FillShares(HouseholdProfile p)
    {
        var total = p.Import.Sum();
        if (total <= 0)
        {
            return;
        }

        double night = 0, evening = 0, peak = 0, winter = 0, zoneNight = 0, np = 0;
        for (var i = 0; i < TariffCalendar.Hours; i++)
        {
            var kwh = p.Import[i];
            var hour = TariffCalendar.Dates[i].Hour;
            if (hour is >= 22 or < 6) night += kwh;
            if (hour is >= 17 and < 22) evening += kwh;
            if (hour is >= 17 and < 21) peak += kwh;
            if (TariffCalendar.Month[i] is >= 10 or <= 1) winter += kwh;
            if (TariffCalendar.Zone2[i] == TariffZone.Night) zoneNight += kwh;
            np += kwh * Catalog.Np[i];
        }

        p.NightShare = night / total;
        p.EveningShare = evening / total;
        p.PeakShare = peak / total;
        p.WinterShare = winter / total;
        p.TwoZoneNightShare = zoneNight / total;
        p.PersonalWeightedPrice = np / total;
        p.MeanSpotPrice = Catalog.NpMean;
    }

    /// <summary>
    /// Yearly kWh from the consumption question: a range (its middle) or a number typed in "Kita"
    /// (kWh per month; above 2 000 it is taken as a yearly figure). Null for "Nežinau" / no answer.
    /// </summary>
    private static double? StatedAnnualKwh(Answer answer, double estimate)
    {
        if (answer.SelectedOptions.Contains(Question.CustomOption) &&
            answer.CustomText is { } text &&
            double.TryParse(new string(text.Where(c => char.IsDigit(c) || c is ',' or '.').ToArray()).Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var typed) && typed > 0)
        {
            return typed > 2000 ? typed : typed * 12;
        }

        return answer.SelectedOptions.FirstOrDefault() switch
        {
            Q.MonthlyLow => 1300,
            Q.MonthlyMid => 2700,
            Q.MonthlyHigh => 5400,
            Q.MonthlyVeryHigh => Math.Max(9000, estimate),
            _ => null
        };
    }

    private static double EstimateAnnualKwh(QuestionnaireState state, double evKwh)
    {
        bool Has(int id, string option) => state.GetAnswer(id).SelectedOptions.Contains(option);
        var first = state.GetAnswer(1).SelectedOptions.FirstOrDefault();
        var isHouse = Q.IsHouseAnswer(first);

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
        if (Has(5, Q.Sauna)) kwh += 800;
        if (Has(5, Q.Pool)) kwh += 1500;
        if (Has(5, Q.Jacuzzi)) kwh += 500;
        if (Has(5, Q.HomeServer)) kwh += 400;
        if (Has(5, Q.CryptoMining)) kwh += 8000;
        if (Has(5, Q.PowerfulPc)) kwh += 500;
        if (Has(5, Q.ElectricStove)) kwh += 300;
        if (Has(5, Q.Recuperation)) kwh += 400;
        if (Has(5, Q.AirConditioner)) kwh += 300;

        return kwh + evKwh;
    }

    // ── Plans ───────────────────────────────────────────────────────────

    /// <summary>Every allowed combination for the profile's own flows, cheapest first.</summary>
    public IReadOnlyList<PlanEstimate> Estimate(HouseholdProfile p) => EstimateFlows(p, p.Import, p.Export);

    /// <summary>
    /// Every allowed combination for the given hourly flows, cheapest first. Prosumers get the
    /// cheapest settlement per combination (two-way metering modes 1–4 or net billing).
    /// </summary>
    /// <param name="lggKw">Permitted generation power for ESO mode 2; defaults to <see cref="HouseholdProfile.LggKw"/>.</param>
    public IReadOnlyList<PlanEstimate> EstimateFlows(HouseholdProfile p, double[] import, double[]? export, Func<PlanCombo, bool>? filter = null, double? lggKw = null)
    {
        var flows = Flows(p, import, export, lggKw);
        var results = new List<PlanEstimate>();
        var totalImport = import.Sum();

        foreach (var combo in Combos(p, filter))
        {
            var (bill, settlement) = Cheapest(flows, combo, p, 1);
            var cost = bill.Total;

            double worst = cost, best = cost;
            if (combo.Supplier.Kind == SupplierKind.Exchange)
            {
                worst = PriceOne(flows, combo, p, _tariffs.SpotStressFactor, settlement).Total;
                best = PriceOne(flows, combo, p, _tariffs.SpotCalmFactor, settlement).Total;
            }

            if (combo.Zones > 1 && p.ShiftPotential > 0)
            {
                // Habits slip back: the realistic downside of time-zone plans.
                worst = Math.Max(worst, PriceOne(flows, combo, p, 1, settlement, shifted: false).Total);
            }

            results.Add(Make(combo, bill, settlement, cost, Math.Max(cost, worst), Math.Min(cost, best), totalImport, p));
        }

        return results.OrderBy(r => r.YearlyCost).ToList();
    }

    /// <summary>Cheapest expected yearly bill over all combinations (used by the solar / battery scans).</summary>
    public double CheapestCost(HouseholdProfile p, double[] import, double[]? export, Func<PlanCombo, bool>? filter = null, double? lggKw = null)
    {
        var flows = Flows(p, import, export, lggKw);
        var best = double.MaxValue;
        foreach (var combo in Combos(p, filter))
        {
            best = Math.Min(best, Cheapest(flows, combo, p, 1).Bill.Total);
        }

        return best;
    }

    /// <summary>Raw and shifted aggregates of one set of flows, plus the LGG used by ESO mode 2.</summary>
    private sealed record FlowSet(FlowAggregates Raw, FlowAggregates Shifted, bool HasExport, double LggKw);

    private FlowSet Flows(HouseholdProfile p, double[] import, double[]? export, double? lggKw = null)
    {
        var np = Catalog.Np;
        var raw = FlowAggregates.From(import, export, np);
        var shifted = p.ShiftPotential > 0 ? FlowAggregates.From(LoadProfileBuilder.Shift(import, p.ShiftPotential), export, np) : raw;
        return new FlowSet(raw, shifted, export is not null && export.Sum() > 1, lggKw ?? p.LggKw);
    }

    private IEnumerable<PlanCombo> Combos(HouseholdProfile p, Func<PlanCombo, bool>? filter) =>
        _engine.Combos(p.HasSmartMeter, includePublic: false)
            .Where(c => filter is null || filter(c));

    private IEnumerable<ProsumerSettlement?> Settlements(HouseholdProfile p, FlowSet flows)
    {
        if (!flows.HasExport)
        {
            yield return null;
            yield break;
        }

        for (var mode = 1; mode <= 3; mode++)
        {
            yield return new ProsumerSettlement(ProsumerScheme.NetMetering, mode, flows.LggKw);
        }

        yield return new ProsumerSettlement(ProsumerScheme.NetBilling, 0, flows.LggKw);
    }

    private (BillBreakdown Bill, ProsumerSettlement? Settlement) Cheapest(FlowSet flows, PlanCombo combo, HouseholdProfile p, double npMultiplier)
    {
        BillBreakdown? best = null;
        ProsumerSettlement? bestSettlement = null;
        foreach (var s in Settlements(p, flows))
        {
            var bill = PriceOne(flows, combo, p, npMultiplier, s);
            if (best is null || bill.Total < best.Total)
            {
                best = bill;
                bestSettlement = s;
            }
        }

        return (best!, bestSettlement);
    }

    private BillBreakdown PriceOne(FlowSet flows, PlanCombo combo, HouseholdProfile p, double npMultiplier, ProsumerSettlement? settlement, bool shifted = true)
    {
        var exchange = combo.Supplier.Kind == SupplierKind.Exchange;
        // Without a smart meter the exchange plan is billed by a standard profile – no shifting benefit.
        var flatNp = exchange && !p.HasSmartMeter;
        var flexible = (exchange && p.HasSmartMeter) || combo.Zones > 1;
        var agg = shifted && flexible ? flows.Shifted : flows.Raw;
        return _engine.Price(agg, combo, p.LgKw, npMultiplier, settlement, flatNp);
    }

    private static PlanEstimate Make(PlanCombo combo, BillBreakdown bill, ProsumerSettlement? settlement, double cost, double worst, double best, double kwh, HouseholdProfile p)
    {
        var spread = cost > 0 ? (worst - cost) / cost : 0;
        var s = combo.Supplier;
        return new PlanEstimate
        {
            Id = CodeOf(combo),
            Type = TypeOf(combo),
            Supplier = s.Supplier,
            PlanName = s.Name,
            EsoPlan = combo.Eso.Id,
            Zones = combo.Zones,
            DurationMonths = s.DurationMonths,
            PublicSupply = s.Kind == SupplierKind.Public,
            Settlement = settlement?.Label,
            Breakdown = bill,
            YearlyCost = Math.Round(cost),
            WorstCost = Math.Round(worst),
            BestCost = Math.Round(best),
            EffectivePrice = kwh > 0 ? cost / kwh : 0,
            Score = cost + p.RiskAversion * (worst - cost),
            RiskLevel = spread switch
            {
                > 0.25 => "Aukšta",
                > 0.06 => "Vidutinė",
                _ => "Žema"
            }
        };
    }

    public static PlanType TypeOf(PlanCombo c) =>
        c.Supplier.Kind == SupplierKind.Exchange ? PlanType.Exchange : c.Zones > 1 ? PlanType.Zoned : PlanType.Fixed;

    /// <summary>The allowed combination with this <see cref="CodeOf"/> id, if any.</summary>
    public PlanCombo? ComboOf(HouseholdProfile p, string id) => Combos(p, null).FirstOrDefault(c => CodeOf(c) == id);

    /// <summary>Bill of every prosumer settlement (two-way modes 1–4, net billing) for one combination.</summary>
    public IReadOnlyList<(ProsumerSettlement Settlement, BillBreakdown Bill)> SettlementOptions(HouseholdProfile p, double[] import, double[] export, string comboId, double? lggKw = null)
    {
        var combo = ComboOf(p, comboId);
        if (combo is null)
        {
            return Array.Empty<(ProsumerSettlement, BillBreakdown)>();
        }

        var flows = Flows(p, import, export, lggKw);
        return Settlements(p, flows)
            .Where(x => x is not null)
            .Select(x => (x!, PriceOne(flows, combo, p, 1, x)))
            .ToList();
    }

    /// <summary>Short id, e.g. "N2:ENE-F12z2" (ESO plan initial + zones : supplier, kind, months, zones).</summary>
    public static string CodeOf(PlanCombo c)
    {
        var s = c.Supplier;
        var supplier = s.Supplier.Length >= 3 ? s.Supplier[..3].ToUpperInvariant() : s.Supplier.ToUpperInvariant();
        var kind = s.Kind switch
        {
            SupplierKind.Exchange => "B",
            SupplierKind.Public => "V",
            _ => $"F{s.DurationMonths}"
        };
        var zones = s.Kind != SupplierKind.Exchange && s.Zones > 1 ? $"z{s.Zones}" : "";
        return $"{c.Eso.Id[0]}{c.Zones}:{supplier}-{kind}{zones}";
    }

    /// <summary>
    /// Best combination of every plan type (fixed 1 zone, time zones, exchange), best score first –
    /// the cards on the results page.
    /// </summary>
    public static IReadOnlyList<PlanEstimate> BestPerType(IEnumerable<PlanEstimate> estimates) =>
        estimates.GroupBy(e => e.Type)
            .Select(g => g.OrderBy(e => e.Score).First())
            .OrderBy(e => e.Score)
            .ToList();

    /// <summary>
    /// The usual plan savings are shown against: ESO Standartinis, 1 zona + the cheapest fixed 1-zone
    /// supplier plan (the current plan is not asked in the scheme).
    /// </summary>
    public static PlanEstimate? ReferencePlan(IEnumerable<PlanEstimate> estimates) =>
        estimates.Where(e => e.EsoPlan == "Standartinis" && e.Zones == 1 && e.Type == PlanType.Fixed)
            .MinBy(e => e.YearlyCost);

    /// <summary>The recommended combination: lowest cost adjusted by the user's risk tolerance.</summary>
    public static PlanEstimate Recommended(IEnumerable<PlanEstimate> estimates) => estimates.OrderBy(e => e.Score).First();

    public static string FullLabel(PlanEstimate e) => $"{e.EsoLabel} + {e.PlanName}";

    // ── Explanations ────────────────────────────────────────────────────

    /// <summary>ESO-only yearly cost of every ESO plan × zones for the profile's import (VIAP/DED excluded).</summary>
    public IReadOnlyList<(string Label, double Eur)> EsoOnlyCosts(HouseholdProfile p)
    {
        var agg = FlowAggregates.From(p.Import, null, Catalog.Np);
        var result = new List<(string, double)>();
        foreach (var plan in Catalog.Eso.Plans)
        {
            foreach (var (zones, prices) in plan.Zones.OrderBy(z => z.Key))
            {
                if (zones == 4 && !p.HasSmartMeter)
                {
                    continue;
                }

                var eur = plan.MonthlyFee(p.LgKw) * 12 + TariffCalendar.ZoneList(zones).Sum(z => agg.ZoneKwhYear(zones, z) * prices[z]);
                result.Add(($"{plan.Id}, {PlanCombo.ZonesLabel(zones)}", eur));
            }
        }

        return result.OrderBy(r => r.Item2).ToList();
    }

    /// <summary>Why one ESO plan suits the user (thresholds from the documentation).</summary>
    public IReadOnlyList<string> EsoInsights(HouseholdProfile p)
    {
        var lines = new List<string>();
        var eso = EsoOnlyCosts(p);
        var cheapestEso = eso.Count > 0 ? eso[0].Label : string.Empty;
        var standard = Catalog.Eso.Plans.FirstOrDefault(x => x.Id == "Standartinis");
        var home = Catalog.Eso.Plans.FirstOrDefault(x => x.Id == "Namai");
        var effective = Catalog.Eso.Plans.FirstOrDefault(x => x.Id == "Efektyvus");
        var kwh = p.PurchasedKwh;

        if (standard is not null && home is not null && standard.Zones.TryGetValue(1, out var s1) && home.Zones.TryGetValue(1, out var h1))
        {
            var diff = s1[TariffZone.All] - h1[TariffZone.All];
            var threshold = diff > 0 ? home.FixedMonthly * 12 / diff : double.MaxValue;
            lines.Add(kwh > threshold
                ? $"Per metus iš tinklo paimate ~{N0(kwh)} kWh – daugiau nei {N0(threshold)} kWh, todėl ESO plane „Namai“ kWh pigesnė atperka {home.FixedMonthly.ToString("0.00", Lt)} € mėnesio mokestį."
                : $"Per metus iš tinklo paimate ~{N0(kwh)} kWh – mažiau nei {N0(threshold)} kWh, todėl ESO „Standartinis“ (be mėnesio mokesčio) pigesnis už „Namai“.");
        }

        if (standard is not null && effective is not null && standard.Zones.TryGetValue(1, out var st) && effective.Zones.TryGetValue(1, out var ef))
        {
            var diff = st[TariffZone.All] - ef[TariffZone.All];
            var perLg = diff > 0 ? effective.FixedPerLgKw / diff : double.MaxValue;
            var ratio = kwh / 12 / Math.Max(0.1, p.LgKw);
            lines.Add(ratio > perLg
                ? $"Per mėnesį suvartojate ~{N0(kwh / 12)} kWh, o leistinoji galia {p.LgKw.ToString("0.#", Lt)} kW: {ratio.ToString("0.#", Lt)} kWh/kW > {perLg.ToString("0.#", Lt)} – ESO „Efektyvus“ pigesnis už „Standartinį“" +
                  (cheapestEso.StartsWith("Efektyvus", StringComparison.Ordinal) || cheapestEso.Length == 0 ? "." : $", bet jums dar pigesnis „{cheapestEso.Split(',')[0]}“.")
                : $"„Efektyvus“ neapsimoka: {ratio.ToString("0.#", Lt)} kWh per mėnesį vienam leistinosios galios kW (reikia > {perLg.ToString("0.#", Lt)}; LG {p.LgKw.ToString("0.#", Lt)} kW{(p.LgFromEso ? " pagal ESO" : ", prielaida")}).");
        }

        if (standard is not null && standard.Zones.TryGetValue(2, out var s2) && standard.Zones.TryGetValue(1, out var s1z))
        {
            var day = s2[TariffZone.Day];
            var night = s2[TariffZone.Night];
            var threshold = (day - s1z[TariffZone.All]) / (day - night);
            lines.Add(p.TwoZoneNightShare > threshold
                ? $"Naktį, savaitgaliais ir švenčių dienomis suvartojate {P0(p.TwoZoneNightShare)} – daugiau nei {P0(threshold)}, todėl 2 zonos pigesnės už 1 zoną."
                : $"Naktį, savaitgaliais ir švenčių dienomis suvartojate {P0(p.TwoZoneNightShare)} – mažiau nei {P0(threshold)}, todėl 2 zonos neapsimoka.");
        }

        if (eso.Count > 0)
        {
            lines.Add($"Pigiausia ESO dalis jums: {eso[0].Label} (~{N0(eso[0].Eur)} €/m.); brangiausia – {eso[^1].Label} (~{N0(eso[^1].Eur)} €/m.).");
        }

        return lines;
    }

    /// <summary>
    /// Why the offered combination suits the user, in plain words (no thresholds, no jargon) – for the top
    /// of the results page and the rule-based recommendation. The technical reasons are <see cref="EsoInsights"/>.
    /// </summary>
    public IReadOnlyList<string> SimpleReasons(HouseholdProfile p, PlanEstimate offer)
    {
        var reasons = new List<string>();
        var perMonth = N0(p.PurchasedKwh / 12) + (p.ConsumptionFromFile || p.ConsumptionSource.StartsWith("pagal jūsų") ? "" : ", apytiksliai");
        reasons.Add(offer.EsoPlan switch
        {
            "Namai" => $"Elektros sunaudojate nemažai (~{perMonth} kWh per mėnesį), todėl apsimoka ESO planas „Namai“: mokate 3 € per mėnesį, bet kiekviena kWh pigesnė.",
            "Efektyvus" => $"Elektros sunaudojate labai daug (~{perMonth} kWh per mėnesį), todėl apsimoka ESO planas „Efektyvus“: mėnesio mokestis didesnis, bet kiekviena kWh pigiausia.",
            _ => $"Elektros sunaudojate nedaug (~{perMonth} kWh per mėnesį), todėl tinka ESO planas „Standartinis“ – be mėnesio mokesčio."
        });
        reasons.Add(offer.Zones switch
        {
            2 => $"Maždaug {(p.TwoZoneNightShare * 100).ToString("0", Lt)} % elektros sunaudojate naktį ir savaitgaliais, kai ji pigesnė – todėl dvi laiko zonos jums pigiau.",
            4 => "Mažai elektros naudojate brangiausiu vakaro metu, todėl apsimoka keturios laiko zonos.",
            _ => "Elektrą naudojate tolygiai visą parą, todėl paprasčiausias vienos zonos planas jums tinka geriausiai."
        });
        reasons.Add(offer.Type == PlanType.Exchange
            ? "Biržos kaina jums vidutiniškai pigiausia, bet ji kinta – kai kuriais mėnesiais sąskaita gali būti didesnė."
            : "Fiksuota kaina – sąskaita nuspėjama ir nereikia sekti biržos" + (p.RiskAversion >= 0.7 ? ", o jums tai svarbu." : "."));
        if (offer.Type == PlanType.Exchange && p.WinterShare > 0.45)
        {
            reasons.Add("Atsargiai: daug elektros naudojate žiemą, kai birža brangiausia.");
        }

        return reasons;
    }

    /// <summary>When the exchange plan is risky for this household.</summary>
    public IReadOnlyList<string> RiskWarnings(HouseholdProfile p)
    {
        var warnings = new List<string>();
        if (p.PeakShare > 0.30)
        {
            warnings.Add($"Daug vartojate 17–21 val. ({P0(p.PeakShare)}) – tada biržos kaina brangiausia.");
        }

        if (p.WinterShare > 0.45)
        {
            warnings.Add($"Žiemos mėnesiais (lapkritį–vasarį) sunaudojate {P0(p.WinterShare)} visų metų elektros – šaltomis, nevėjuotomis dienomis biržos kaina labai pakyla.");
        }

        if (p.MeanSpotPrice > 0 && p.PersonalWeightedPrice > p.MeanSpotPrice * 1.05)
        {
            warnings.Add($"Jūsų asmeninė svertinė biržos kaina ({Ct(p.PersonalWeightedPrice)}) didesnė už metų vidurkį ({Ct(p.MeanSpotPrice)}) – vartojate brangesniu laiku.");
        }

        return warnings;
    }

    // ── Text for the advisor and the rule-based fallback ────────────────

    public string Describe(HouseholdProfile p, IReadOnlyList<PlanEstimate> estimates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("PROFILIS:");
        sb.AppendLine($"- Metinis suvartojimas: {p.AnnualKwh:0} kWh{(p.FlowsFromFile ? " (valandiniai ESO duomenys)" : p.ConsumptionFromFile ? " (iš įkelto failo)" : " (įvertinta)")}; iš tinklo: {p.PurchasedKwh:0} kWh" +
                      (p.Export is { } ex ? $"; į tinklą: {ex.Sum():0} kWh" : ""));
        sb.AppendLine($"- Iš tinklo paimamos energijos: naktį (22–6) {p.NightShare:P0}, vakare (17–22) {p.EveningShare:P0}, 17–21 val. {p.PeakShare:P0}, žiemos mėn. (lapkritis–vasaris) {p.WinterShare:P0}, 2 zonų naktis+savaitgaliai {p.TwoZoneNightShare:P0}");
        sb.AppendLine($"- Perkeliama dalis į pigias valandas: {p.ShiftPotential:P0}; išmanusis skaitiklis: {(p.HasSmartMeter ? "yra" : "nėra/nežinoma (4 zonos atmestos)")}; LG {p.LgKw:0.#} kW{(p.LgFromEso ? " (ESO)" : " (prielaida)")}");
        sb.AppendLine($"- ASMENINĖ SVERTINĖ BIRŽOS KAINA: {p.PersonalWeightedPrice:0.0000} €/kWh be PVM (metų vidurkis {p.MeanSpotPrice:0.0000}, kainos {Catalog.NpPeriodLabel}).");
        sb.AppendLine($"- Rizikos vengimas (0–1): {p.RiskAversion:0.00}; sava gamyba: {(p.HasSolar ? $"taip, ~{p.ExistingKwp:0.#} kWp" : "ne")}; kaupiklis: {(p.HasBattery ? "taip" : "ne")}");
        foreach (var note in p.Notes)
        {
            sb.AppendLine($"- Pastaba: {note}");
        }

        sb.AppendLine("ESO PLANO PARINKIMAS:");
        foreach (var line in EsoInsights(p))
        {
            sb.AppendLine($"- {line}");
        }

        var warnings = RiskWarnings(p);
        if (warnings.Count > 0)
        {
            sb.AppendLine("BIRŽOS RIZIKOS ĮSPĖJIMAI:");
            foreach (var w in warnings)
            {
                sb.AppendLine($"- {w}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"DERINIAI (iš viso {estimates.Count}; PILNA sąskaita = ESO + energija + VIAP/DED + pastovūs mokesčiai{(p.HasSolar ? " ± saulė" : "")}; €/mėn. (€/m.); brangūs / pigūs metai; rizika). TOP 12 pagal balą:");
        foreach (var e in estimates.OrderBy(e => e.Score).Take(12))
        {
            sb.AppendLine($"- [{e.Id}] {FullLabel(e)}: {e.MonthlyCost:0} €/mėn. ({e.YearlyCost:0} €/m.); {e.WorstCost:0} / {e.BestCost:0}; rizika {e.RiskLevel}{Split(e)}");
        }

        var best = BestPerType(estimates);
        sb.AppendLine("GERIAUSIAS KIEKVIENO TIPO: " + string.Join("; ", best.Select(e => $"{Recommendation.PlanTypeLabel(e.Type)} – [{e.Id}] {e.MonthlyCost:0} €/mėn.")));
        var top = Recommended(estimates);
        sb.AppendLine($"SKAIČIUOKLĖS SIŪLYMAS (kaina + rizikos tolerancija): [{top.Id}] {FullLabel(top)}.");
        return sb.ToString();
    }

    private static string Split(PlanEstimate e)
    {
        if (e.Breakdown is not { } b)
        {
            return "";
        }

        var text = $" [ESO {b.EsoLines.Sum(l => l.Eur):0}, energija {b.Energy:0}, pastovūs {b.EsoFixed + b.SupplierFee:0}, VIAP+DED {b.ViapDed:0}";
        if (e.Settlement is { } s)
        {
            text += $"; {s}: ESO mokestis {b.ProsumerFee:0}, atsiimta {b.TakenBackKwh:0} kWh{(b.WalletUsed < 0 ? $", piniginė {b.WalletUsed:0}" : "")}";
        }

        return text + "]";
    }

    /// <summary>Recommendation without the language model (no API key or the model failed).</summary>
    public Recommendation RuleBasedRecommendation(HouseholdProfile p, IReadOnlyList<PlanEstimate> estimates)
    {
        var top = Recommended(estimates);
        var best = BestPerType(estimates);
        var fixedPlan = best.FirstOrDefault(e => e.Type == PlanType.Fixed);

        var sb = new StringBuilder();
        sb.AppendLine($"**Rekomendacija:** {top.EsoLabel} + {top.PlanName}.");
        sb.AppendLine();
        sb.AppendLine("**Kodėl jums:**");
        foreach (var line in SimpleReasons(p, top))
        {
            sb.AppendLine($"- {line}");
        }

        sb.AppendLine();

        if (top.Breakdown is { } b)
        {
            sb.AppendLine($"**Kiek kainuotų:** ~{N0(top.MonthlyCost)} € per mėnesį (~{N0(top.YearlyCost)} € per metus): " +
                          $"už laidus (ESO) ~{N0((b.EsoLines.Sum(l => l.Eur) + b.EsoFixed) / 12)} €, už elektrą ~{N0((b.Energy + b.SupplierFee) / 12)} € per mėnesį.");
        }

        if (ReferencePlan(estimates) is { } reference && reference.Id != top.Id)
        {
            var saved = reference.YearlyCost - top.YearlyCost;
            sb.AppendLine($"Palyginti su įprastu planu ({reference.EsoLabel} + {reference.PlanName}) sutaupoma ~{N0(saved / 12)} € per mėnesį (~{N0(saved)} € per metus).");
        }

        if (fixedPlan is not null && fixedPlan.Id != top.Id)
        {
            sb.AppendLine($"Geriausias fiksuotas 1 zonos variantas ({fixedPlan.PlanName}) – ~{N0(fixedPlan.MonthlyCost)} €/mėn.");
        }

        var warnings = RiskWarnings(p);
        if (top.Type == PlanType.Exchange)
        {
            sb.AppendLine();
            sb.AppendLine($"**Rizikos:** brangiais metais gali kainuoti iki ~{N0(top.WorstCost / 12)} €/mėn.");
            foreach (var w in warnings)
            {
                sb.AppendLine($"- {w}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("*Tiekėjų kainos – DEMO, ESO tarifai – 2026-01-01. Prieš sudarydami sutartį pasitikrinkite aktualias sąlygas.*");

        return new Recommendation
        {
            PlanType = top.Type,
            Supplier = top.Supplier,
            PlanId = top.Id,
            PlanLabel = FullLabel(top),
            SummaryMarkdown = sb.ToString(),
            Confidence = p.FlowsFromFile ? "Aukštas" : "Vidutinis",
            ByAi = false
        };
    }

    private static string N0(double v) => v.ToString("#,0", Lt);

    private static string P0(double share) => (share * 100).ToString("0.#", Lt) + " %";

    private static string Ct(double eurPerKwh) => eurPerKwh.ToString("0.000", Lt) + " €/kWh";
}
