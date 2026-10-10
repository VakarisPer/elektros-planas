using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using vamos.Models;

namespace vamos.Services;

public enum SolarMode
{
    /// <summary>House without a plant, interested in solar panels (with or without a battery).</summary>
    NewPlant,

    /// <summary>Owns a plant without a battery – would a battery pay off?</summary>
    AddBattery,

    /// <summary>Owns a plant and a battery – what does the battery bring?</summary>
    HasBattery,

    /// <summary>House interested only in a battery (or less than 5 kW can be connected): charged from the grid when cheap.</summary>
    BatteryOnly
}

/// <summary>Yearly bill of one prosumer settlement for the recommended combination.</summary>
public sealed record SettlementRow(string Label, double YearlyCost, double LeftoverKwh, bool Best);

/// <summary>Everything the results page, the AI tool and the fallback need about solar / battery.</summary>
public sealed class SolarReport
{
    public SolarMode Mode { get; init; }
    public double ExistingKwp { get; init; }
    public bool LoadFromFile { get; init; }

    /// <summary>Plan type and combination that are cheapest after the change.</summary>
    public PlanType PlanType { get; init; }
    public string PlanLabel { get; init; } = string.Empty;

    public required SolarScan Scan { get; init; }
    public required IReadOnlyDictionary<PlanType, SolarScan> ByType { get; init; }

    /// <summary>Configuration shown as the answer (best plant, or the battery for owners).</summary>
    public required SolarOption Chosen { get; init; }

    public bool WorthIt { get; init; }
    public string Verdict { get; init; } = string.Empty;

    /// <summary>Best combination per plan type – full bill – before and after (same hourly method).</summary>
    public required IReadOnlyList<PlanEstimate> Baseline { get; init; }
    public required IReadOnlyList<PlanEstimate> WithSolar { get; init; }

    /// <summary>Full monthly bill of the cheapest combination before / after.</summary>
    public required double[] MonthlyBillBefore { get; init; }
    public required double[] MonthlyBillAfter { get; init; }

    /// <summary>ESO part (zones, fixed fee, prosumer fee) saved per year.</summary>
    public double EsoSavedPerYear { get; init; }

    /// <summary>Two-way metering modes 1–4 and net billing for the cheapest combination after the change.</summary>
    public IReadOnlyList<SettlementRow> Settlements { get; init; } = Array.Empty<SettlementRow>();

    /// <summary>Less than 5 kW can be connected, so solar panels are not possible.</summary>
    public bool LowGridCapacity { get; init; }

    /// <summary>Battery without a plant: kWh delivered from the battery per year.</summary>
    public double BatteryDeliveredKwh { get; init; }

    public string Assumptions { get; init; } = string.Empty;

    /// <summary>ESO grid connection check used in the evaluation, if any.</summary>
    public EsoConnectionInfo? Eso { get; init; }

    /// <summary>Export cap applied to a new plant (from ESO), kW.</summary>
    public double? ExportLimitKw { get; init; }

    public bool IsBatteryMode => Mode is SolarMode.AddBattery or SolarMode.HasBattery or SolarMode.BatteryOnly;

    public SettlementRow? BestSettlement => Settlements.FirstOrDefault(s => s.Best);

    public string ChosenLabel => Mode switch
    {
        SolarMode.BatteryOnly => $"{Chosen.BattKwh:0.#} kWh kaupiklis be saulės elektrinės",
        SolarMode.AddBattery or SolarMode.HasBattery =>
            $"{Chosen.BattKwh:0.#} kWh kaupiklis prie ~{ExistingKwp:0.#} kWp elektrinės",
        _ => Chosen.BattKwh > 0
            ? $"{Chosen.PvKwp:0.#} kWp elektrinė + {Chosen.BattKwh:0.#} kWh kaupiklis"
            : $"{Chosen.PvKwp:0.#} kWp elektrinė, be kaupiklio"
    };
}

/// <summary>
/// Glue between the survey, <see cref="PlanCalculator"/> and <see cref="SolarSimulator"/>: decides
/// whether solar / a battery is relevant and evaluates plant and battery options. Every
/// configuration is priced with the cheapest allowed plan combination and settlement, so
/// "with solar" may use a different plan than "without".
/// </summary>
public sealed class SolarAdvisor
{
    private static readonly CultureInfo Lt = CultureInfo.GetCultureInfo("lt-LT");

    private readonly PlanCalculator _calculator;
    private readonly SolarOptions _o;

    public SolarAdvisor(PlanCalculator calculator, IOptions<SolarOptions> options)
    {
        _calculator = calculator;
        _o = options.Value;
    }

    /// <summary>
    /// Null when solar / a battery is not relevant: apartments (the scheme has no solar branch for them),
    /// houses that answered "Ne" – unless the ESO file shows an existing plant.
    /// </summary>
    public SolarReport? Evaluate(QuestionnaireState state, double? pvKwp = null, double? batteryKwh = null)
    {
        var p = _calculator.BuildProfile(state);
        var eso = state.EsoConnection is { Success: true } e ? e : null;

        SolarMode mode;
        switch (p.Solar)
        {
            case SolarStatus.Interested when !p.WantsPanels && pvKwp is null:
                return EvaluateBatteryOnly(p, eso, batteryKwh);
            case SolarStatus.Interested:
                mode = SolarMode.NewPlant;
                break;
            case SolarStatus.Own:
                mode = p.Battery == BatteryStatus.Has ? SolarMode.HasBattery : SolarMode.AddBattery;
                break;
            default:
                return null;
        }

        var load = p.Consumption;
        var existingKwp = mode is SolarMode.AddBattery or SolarMode.HasBattery ? p.ExistingKwp ?? 6 : 0;

        // A new plant must respect the export cap ESO reported for this object.
        var exportLimit = mode == SolarMode.NewPlant ? eso?.ExportLimitKw : null;

        // ── Options: panels only, or panels × battery ──
        IReadOnlyList<double> pvOptions = _o.HouseKwpOptions;
        IReadOnlyList<double> battOptions = mode switch
        {
            SolarMode.HasBattery => new[] { 0, _o.AssumedExistingBatteryKwh },
            SolarMode.NewPlant when !p.WantsBattery => new[] { 0.0 },
            _ => _o.HouseBatteryOptions
        };

        // "What if" from the user / model.
        if (pvKwp is { } wantPv && mode == SolarMode.NewPlant)
        {
            pvOptions = new[] { Math.Clamp(wantPv, 0.2, 30) };
        }

        if (batteryKwh is { } wantBatt)
        {
            var b = Math.Clamp(wantBatt, 0, 30);
            battOptions = b > 0 ? new[] { 0, b } : new[] { 0.0 };
        }

        var sim = new SimOptions(_o.Yield, _o.BatteryEff, _o.CRate) { ExportLimitKw = exportLimit };
        // ESO mode 2 is billed by the permitted generation power: the plant itself, or the export cap.
        double Lgg(SimResult r) => exportLimit ?? Math.Max(p.LggKw, r.PvKwp);
        // The scheme: with a battery the tariff is dynamic (exchange) only; panels alone may use any tariff.
        double Cost(SimResult r, Func<PlanCombo, bool>? filter) =>
            _calculator.CheapestCost(p, r.ImportSeries, r.Export > 0.5 ? r.ExportSeries : null,
                c => (r.BattKwh <= 0 || PlanCalculator.TypeOf(c) == PlanType.Exchange) && (filter is null || filter(c)), Lgg(r));

        var scan = SolarSimulator.Analyze(load, pvOptions, battOptions, r => Cost(r, null), _o, sim, _o.PvCostPerKwp, existingKwp);
        var byType = new Dictionary<PlanType, SolarScan>();
        foreach (var type in Enum.GetValues<PlanType>())
        {
            if (!_calculator.Engine.Combos(p.HasSmartMeter, includePublic: false).Any(c => PlanCalculator.TypeOf(c) == type))
            {
                continue;
            }

            // Batteries are not offered with fixed tariffs, so those rows compare plants without one.
            var typeBatteries = type == PlanType.Exchange ? battOptions : new[] { 0.0 };
            if (mode != SolarMode.NewPlant && type != PlanType.Exchange)
            {
                continue;
            }

            byType[type] = SolarSimulator.Analyze(load, pvOptions, typeBatteries,
                r => Cost(r, c => PlanCalculator.TypeOf(c) == type), _o, sim, _o.PvCostPerKwp, existingKwp);
        }

        var chosen = mode switch
        {
            SolarMode.AddBattery or SolarMode.HasBattery => scan.BestWithBattery ?? scan.BestOverall,
            _ => scan.BestOverall
        };

        var before = SolarSimulator.Simulate(load, existingKwp, 0, sim);
        var after = SolarSimulator.Simulate(load, chosen.PvKwp, chosen.BattKwh, sim);
        var beforeEst = _calculator.EstimateFlows(p, before.ImportSeries, before.Export > 0.5 ? before.ExportSeries : null, lggKw: Lgg(before));
        // With a battery the scheme allows only the dynamic tariff.
        var afterEst = _calculator.EstimateFlows(p, after.ImportSeries, after.Export > 0.5 ? after.ExportSeries : null,
            chosen.BattKwh > 0 ? c => PlanCalculator.TypeOf(c) == PlanType.Exchange : null, Lgg(after));
        var cheapestBefore = beforeEst[0];
        var cheapestAfter = afterEst[0];

        var settlements = after.Export > 0.5
            ? _calculator.SettlementOptions(p, after.ImportSeries, after.ExportSeries, cheapestAfter.Id, Lgg(after))
                .Select(x => (x.Settlement.Label, x.Bill.Total, x.Bill.BankLeftoverKwh))
                .ToList()
            : new List<(string, double, double)>();
        var bestSettlement = settlements.Count > 0 ? settlements.Min(s => s.Item2) : 0;

        var worthIt = mode switch
        {
            SolarMode.AddBattery => scan.BatteryWorthIt,
            SolarMode.HasBattery => chosen.BatteryExtraYear1 > 0,
            _ => scan.SolarWorthIt
        };

        return new SolarReport
        {
            Mode = mode,
            ExistingKwp = existingKwp,
            LoadFromFile = p.FlowsFromFile,
            PlanType = cheapestAfter.Type,
            PlanLabel = PlanCalculator.FullLabel(cheapestAfter),
            Scan = scan,
            ByType = byType,
            Chosen = chosen,
            WorthIt = worthIt,
            Verdict = Verdict(mode, scan, chosen, byType, cheapestAfter.Settlement),
            Baseline = PlanCalculator.BestPerType(beforeEst),
            WithSolar = PlanCalculator.BestPerType(afterEst),
            MonthlyBillBefore = cheapestBefore.Breakdown!.PerMonth,
            MonthlyBillAfter = cheapestAfter.Breakdown!.PerMonth,
            EsoSavedPerYear = Math.Round(Math.Max(0, cheapestBefore.Breakdown.EsoTotal - cheapestAfter.Breakdown.EsoTotal)),
            Settlements = settlements.Select(s => new SettlementRow(s.Item1, Math.Round(s.Item2), Math.Round(s.Item3), Math.Abs(s.Item2 - bestSettlement) < 0.01)).ToList(),
            Eso = eso,
            ExportLimitKw = exportLimit,
            Assumptions = Assumptions()
        };
    }

    /// <summary>
    /// Battery without a plant (documentation: "Kaupiklis be elektrinės"): for the cheapest time-varying
    /// combinations the battery is charged in cheap hours and used in expensive ones; the saving is
    /// compared with the household's cheapest plan without a battery.
    /// </summary>
    private SolarReport EvaluateBatteryOnly(HouseholdProfile p, EsoConnectionInfo? eso, double? batteryKwh)
    {
        var baseline = _calculator.Estimate(p);
        var cheapestBefore = baseline[0];
        var sizes = batteryKwh is > 0 ? new[] { Math.Clamp(batteryKwh.Value, 1, 30) } : _o.HouseBatteryOptions.Where(b => b > 0).ToArray();

        // The scheme: a battery goes with the dynamic (exchange) tariff only.
        var candidates = baseline
            .Where(x => x.Type == PlanType.Exchange)
            .GroupBy(x => (x.EsoPlan, x.Zones))
            .Select(g => g.First())
            .Take(8)
            .ToList();

        var zero = new SolarOption { PvKwp = 0, BattKwh = 0 };
        var options = new List<(SolarOption Option, PlanEstimate Plan, ArbitrageResult Run)>();
        foreach (var size in sizes)
        {
            foreach (var candidate in candidates)
            {
                var combo = _calculator.ComboOf(p, candidate.Id);
                if (combo is null)
                {
                    continue;
                }

                var run = BatteryArbitrage.Run(p.Import, _calculator.Engine.HourlyPrice(combo), size, size * _o.CRate, _o.BatteryEff);
                var withBattery = _calculator.EstimateFlows(p, run.ImportSeries, null, c => PlanCalculator.CodeOf(c) == candidate.Id)[0];
                var saving = cheapestBefore.YearlyCost - withBattery.YearlyCost;
                var gross = size * _o.BatteryCostPerKwh + _o.BatteryRetrofitExtra;
                var subsidy = SolarSimulator.Apva(0, size, _o);
                var (npv, payback) = SolarSimulator.Cashflow(gross - subsidy, 0, saving, size, 0, _o);
                options.Add((new SolarOption
                {
                    PvKwp = 0,
                    BattKwh = size,
                    GrossCapex = Math.Round(gross),
                    Subsidy = Math.Round(subsidy),
                    Capex = Math.Round(gross - subsidy),
                    Year1Savings = Math.Round(saving),
                    BatteryExtraYear1 = Math.Round(saving),
                    PaybackYears = payback is null ? null : Math.Round(payback.Value, 1),
                    Npv = Math.Round(npv)
                }, withBattery, run));
            }
        }

        SolarScan ScanOf(IEnumerable<(SolarOption Option, PlanEstimate Plan, ArbitrageResult Run)> list)
        {
            var all = list.Select(x => x.Option).OrderByDescending(o => o.Npv).ToList();
            var bestBattery = all.FirstOrDefault();
            return new SolarScan
            {
                BestOverall = bestBattery is { Npv: > 0 } ? bestBattery : zero,
                BestPvOnly = zero,
                BestWithBattery = bestBattery,
                SolarWorthIt = false,
                BatteryWorthIt = bestBattery is { Npv: > 100 },
                All = all
            };
        }

        var scan = ScanOf(options);
        var byType = options.GroupBy(x => x.Plan.Type).ToDictionary(g => g.Key, ScanOf);
        var best = options.OrderByDescending(x => x.Option.Npv).FirstOrDefault();
        var afterPlan = best.Plan ?? cheapestBefore;
        var chosen = best.Option ?? zero;

        var why = p.LowGridCapacity ? "Jūsų objekte galima prijungti mažiau nei 5 kW, todėl saulės elektrinė negalima – vertinamas tik kaupiklis (su dinaminiu tarifu). " : "";
        string verdict;
        if (best.Option is null)
        {
            verdict = why + "Kaupiklis naudos neduoda – nėra tinkamo dinaminio tarifo derinio.";
        }
        else if (scan.BatteryWorthIt)
        {
            verdict = why + $"Kaupiklis apsimoka su planu {afterPlan.PlanName}: {chosen.BattKwh:0} kWh sutaupo ~{N0(chosen.Year1Savings)} € per metus, {Payback(chosen)}";
        }
        else
        {
            verdict = why + $"Kaupiklis neatsiperka: geriausiu atveju ({chosen.BattKwh:0} kWh, {afterPlan.PlanName}) sutaupytų ~{N0(chosen.Year1Savings)} € per metus, o kainuotų ~{N0(chosen.Capex)} € (su APVA).";
        }

        return new SolarReport
        {
            Mode = SolarMode.BatteryOnly,
            LoadFromFile = p.FlowsFromFile,
            PlanType = afterPlan.Type,
            PlanLabel = PlanCalculator.FullLabel(afterPlan),
            Scan = scan,
            ByType = byType,
            Chosen = chosen,
            WorthIt = scan.BatteryWorthIt,
            LowGridCapacity = p.LowGridCapacity,
            Verdict = verdict,
            Baseline = PlanCalculator.BestPerType(baseline),
            WithSolar = best.Plan is null ? PlanCalculator.BestPerType(baseline) : new[] { best.Plan },
            MonthlyBillBefore = cheapestBefore.Breakdown!.PerMonth,
            MonthlyBillAfter = afterPlan.Breakdown!.PerMonth,
            EsoSavedPerYear = Math.Round(Math.Max(0, cheapestBefore.Breakdown.EsoTotal - afterPlan.Breakdown.EsoTotal)),
            BatteryDeliveredKwh = Math.Round(best.Run?.DeliveredKwh ?? 0),
            Eso = eso,
            Assumptions = Assumptions()
        };
    }

    private string Assumptions() =>
        $"DEMO prielaidos: elektrinė {_o.PvCostPerKwp:0} €/kWp, kaupiklis {_o.BatteryCostPerKwh:0} €/kWh (+{_o.BatteryRetrofitExtra:0} € įrengimas), " +
        $"η = {_o.BatteryEff:0.00}, galia = talpa × {_o.CRate:0.#}, gamyba ~{_o.Yield:0} kWh/kWp, kainų augimas {_o.PriceGrowth:P0}/m., {_o.Horizon} m., be APVA kompensacijų. " +
        "ESO tarifai ir gaminančių vartotojų mokesčiai – 2026 m.; biržos kainos – tikros LT valandinės kainos.";

    private static string Payback(SolarOption o) => o.PaybackYears is { } y ? $"atsiperka per ~{y.ToString("0.#", Lt)} m." : "per 25 m. neatsiperka";

    private static string Verdict(SolarMode mode, SolarScan scan, SolarOption chosen, IReadOnlyDictionary<PlanType, SolarScan> byType, string? settlement)
    {
        switch (mode)
        {
            case SolarMode.AddBattery:
            {
                if (scan.BatteryWorthIt)
                {
                    return $"Kaupiklis verta: {chosen.BattKwh:0} kWh {Payback(chosen)}";
                }

                if (chosen.BatteryExtraYear1 <= 0)
                {
                    return "Kaupiklis neapsimoka: jis vertinamas tik su dinaminiu (biržos) tarifu, o su juo jūsų sąskaita nebūtų mažesnė nei su geriausiu planu be kaupiklio.";
                }

                var twoWay = settlement?.StartsWith("Dvipusė", StringComparison.Ordinal) == true;
                return twoWay
                    ? $"Kaupiklis neatsiperka – su dvipuse apskaita tinklas jau atlieka „baterijos“ vaidmenį (atgauta kWh kainuoja tik ESO mokestį), todėl sutaupytų tik ~{N0(chosen.BatteryExtraYear1)} € per metus."
                    : $"Kaupiklis neatsiperka – sutaupytų tik ~{N0(chosen.BatteryExtraYear1)} € per metus, o kainuotų ~{N0(chosen.Capex)} €.";
            }
            case SolarMode.HasBattery:
                return chosen.BatteryExtraYear1 > 0
                    ? $"Jūsų kaupiklis sutaupo ~{N0(chosen.BatteryExtraYear1)} € per metus."
                    : "Jūsų kaupiklis su dvipuse apskaita beveik nieko nesutaupo – daugiau naudos jis duotų su grynuoju atsiskaitymu.";
            default:
                if (!scan.SolarWorthIt)
                {
                    return "Pagal šias prielaidas saulės elektrinė per 25 m. neatsiperka.";
                }

                return scan.BatteryWorthIt
                    ? $"Verta saulės elektrinė IR kaupiklis: {chosen.PvKwp:0.#} kWp + {chosen.BattKwh:0} kWh {Payback(chosen)}"
                    : $"Verta saulės elektrinė ({chosen.PvKwp:0.#} kWp), {Payback(chosen)}; kaupiklis neatsiperka.";
        }
    }

    /// <summary>Compact text for the language model (all numbers precomputed).</summary>
    public string Describe(SolarReport r)
    {
        static string Eur(double v) => v.ToString("#,0", Lt) + " €";
        static string Opt(SolarOption o) =>
            $"{o.PvKwp:0.#} kWp + {o.BattKwh:0.#} kWh: įranga ~{Eur(o.GrossCapex)}, APVA −{Eur(o.Subsidy)}, galutinė investicija {Eur(o.Capex)}, 1 m. sutaupymas {Eur(o.Year1Savings)}, " +
            $"atsipirkimas {(o.PaybackYears is { } y ? y.ToString("0.#", Lt) + " m." : "neatsiperka")}, NPV {Eur(o.Npv)}" +
            (o.PvKwp > 0 ? $", savarankiškumas {o.SelfSufficiencyPct:0} %" : "");

        var sb = new StringBuilder();
        sb.AppendLine($"SAULĖ/KAUPIKLIS ({r.Mode}; pigiausias derinys po pakeitimo: {r.PlanLabel}; duomenys {(r.LoadFromFile ? "iš ESO failo" : "įvertinti")}{(r.ExistingKwp > 0 ? $"; esama elektrinė ~{r.ExistingKwp:0.#} kWp" : "")}):");
        sb.AppendLine($"- VERDIKTAS: {r.Verdict}");
        if (r.Eso is { } eso)
        {
            sb.AppendLine($"- ESO (obj. {eso.ObjectNumber}): įrengta {eso.InstalledGenerationKw:0.##} kW, leistina generuoti {eso.AllowedGenerationKw:0.##} kW" +
                          $"{(r.ExportLimitKw is { } lim ? $"; naujai elektrinei atidavimas į tinklą ribojamas iki {lim:0.#} kW (įskaičiuota)" : "")}. ESO: {eso.Message}");
        }

        sb.AppendLine($"- Pasirinkta: {r.ChosenLabel}. {Opt(r.Chosen)}");
        if (r.Mode == SolarMode.NewPlant && r.Scan.BestPvOnly != r.Chosen)
        {
            sb.AppendLine($"- Be kaupiklio geriausia: {Opt(r.Scan.BestPvOnly)}");
        }

        if (r.Scan.BestWithBattery is { } wb && wb != r.Chosen)
        {
            sb.AppendLine($"- Su kaupikliu geriausia: {Opt(wb)}");
        }

        if (r.Mode == SolarMode.BatteryOnly)
        {
            sb.AppendLine($"- Kaupiklis per metus atiduoda namams ~{r.BatteryDeliveredKwh:0} kWh, įkrautų pigiu metu (tik dinaminis tarifas).");
        }

        if (r.LowGridCapacity)
        {
            sb.AppendLine("- Galima apkrova < 5 kW: saulės elektrinė negalima, todėl vertinamas tik kaupiklis.");
        }

        sb.AppendLine("- Pagal tarifo tipą (kaupiklis – tik su dinaminiu; NPV su kaupikliu vs be): " + string.Join("; ", r.ByType.Select(kv =>
            $"{kv.Key}: {(kv.Value.BestWithBattery is { } b ? $"{b.BattKwh:0} kWh {Eur(b.Npv)}" : "—")} vs {Eur(kv.Value.BestPvOnly.Npv)}")));

        if (r.Settlements.Count > 0)
        {
            sb.AppendLine("- Energijos pasaugojimo tipas (metinė sąskaita su pigiausiu deriniu): " + string.Join("; ", r.Settlements.Select(s =>
                $"{s.Label} {Eur(s.YearlyCost)}{(s.LeftoverKwh > 0 ? $" (banke lieka {s.LeftoverKwh:0} kWh)" : "")}{(s.Best ? " ← pigiausia" : "")}")));
            sb.AppendLine("- ESO būdą galima keisti kartą per 12 mėn., schemą – kartą per kaupimo laikotarpį (2026-04-01–2028-03-31).");
        }

        sb.AppendLine("- Visa sąskaita po pakeitimo (geriausias kiekvieno tipo): " + string.Join("; ", r.WithSolar.Select(e =>
        {
            var before = r.Baseline.FirstOrDefault(b => b.Type == e.Type);
            return $"{e.Type} [{e.Id}] {Eur(e.MonthlyCost)}/mėn. ({Eur(e.YearlyCost)}/m.){(before is null ? "" : $", buvo {Eur(before.MonthlyCost)}/mėn.")}";
        })));
        sb.AppendLine($"- Sutaupoma ESO dalis ~{Eur(r.EsoSavedPerYear)}/m. (jau įskaičiuota į sąskaitą).");

        sb.AppendLine($"- {r.Assumptions}");
        return sb.ToString();
    }

    /// <summary>Short Markdown paragraph for the rule-based recommendation.</summary>
    public string Paragraph(SolarReport r)
    {
        var o = r.Chosen;
        var title = r.IsBatteryMode ? "Kaupiklis" : "Saulės elektrinė";
        var sb = new StringBuilder();
        sb.AppendLine($"**{title}:** {r.Verdict}");
        if (r.Mode != SolarMode.HasBattery && o.Capex > 0)
        {
            sb.AppendLine($"- {r.ChosenLabel}: įranga ~{N0(o.GrossCapex)} €, APVA kompensacija −{N0(o.Subsidy)} €, galutinė investicija ~{N0(o.Capex)} €; " +
                          $"pirmais metais sutaupytumėte ~{N0(o.Year1Savings)} €, {(o.PaybackYears is { } y ? $"atsipirktų per ~{y.ToString("0.#", Lt)} m." : "per 25 m. neatsipirktų")}.");
        }

        sb.AppendLine($"- Vidutinė sąskaita: ~{N0(r.MonthlyBillAfter.Sum() / 12)} €/mėn. vietoj ~{N0(r.MonthlyBillBefore.Sum() / 12)} €/mėn. (su ESO dalimi).");
        if (r.BestSettlement is { } s)
        {
            sb.AppendLine($"- Pigiausias energijos pasaugojimo būdas: {s.Label} ({r.PlanLabel}).");
        }

        return sb.ToString();
    }

    private static string N0(double v) => v.ToString("#,0", Lt);
}
