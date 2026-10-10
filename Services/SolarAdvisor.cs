using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using vamos.Models;

namespace vamos.Services;

public enum SolarMode
{
    /// <summary>House without a plant, interested in one.</summary>
    NewPlant,

    /// <summary>Apartment: balcony panels or a remote park share.</summary>
    BalconyOrPark,

    /// <summary>Owns a plant without a battery – would a battery pay off?</summary>
    AddBattery,

    /// <summary>Owns a plant and a battery – what does the battery bring?</summary>
    HasBattery
}

/// <summary>Everything the results page, the AI tool and the fallback need about solar / battery.</summary>
public sealed class SolarReport
{
    public SolarMode Mode { get; init; }
    public ProsumerScheme Scheme { get; init; }
    public double ExistingKwp { get; init; }
    public bool LoadFromFile { get; init; }

    /// <summary>Plan type the investment is evaluated with (the recommended one).</summary>
    public PlanType PlanType { get; init; }
    public string Supplier { get; init; } = string.Empty;

    public required SolarScan Scan { get; init; }
    public required IReadOnlyDictionary<PlanType, SolarScan> ByType { get; init; }

    /// <summary>Configuration shown as the answer (best plant, or the battery for owners).</summary>
    public required SolarOption Chosen { get; init; }

    public bool WorthIt { get; init; }
    public string Verdict { get; init; } = string.Empty;

    /// <summary>Best plan per type, full bill (with ESO charges), before and after (same hourly method).</summary>
    public required IReadOnlyList<PlanEstimate> Baseline { get; init; }
    public required IReadOnlyList<PlanEstimate> WithSolar { get; init; }

    /// <summary>Full monthly bill (energy + distribution + fees) for <see cref="PlanType"/>.</summary>
    public required double[] MonthlyBillBefore { get; init; }
    public required double[] MonthlyBillAfter { get; init; }

    public double DistributionSavedPerYear { get; init; }

    /// <summary>Apartments: remote park share alternative.</summary>
    public SolarScan? Park { get; init; }

    public string Assumptions { get; init; } = string.Empty;

    public string ChosenLabel => Mode switch
    {
        SolarMode.AddBattery or SolarMode.HasBattery =>
            $"{Chosen.BattKwh:0.#} kWh kaupiklis prie ~{ExistingKwp:0.#} kWp elektrinės",
        _ => Chosen.BattKwh > 0
            ? $"{Chosen.PvKwp:0.#} kWp elektrinė + {Chosen.BattKwh:0.#} kWh kaupiklis"
            : $"{Chosen.PvKwp:0.#} kWp elektrinė, be kaupiklio"
    };
}

/// <summary>
/// Glue between the survey, <see cref="PlanCalculator"/> and <see cref="SolarSimulator"/>: decides
/// whether solar is relevant, builds the hourly load and evaluates plant / battery options for
/// every plan type.
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

    /// <summary>Null when solar is not relevant (no interest, remote park only, Q8 unanswered).</summary>
    public SolarReport? Evaluate(QuestionnaireState state, double? pvKwp = null, double? batteryKwh = null)
    {
        var p = _calculator.BuildProfile(state);
        var unknownHousing = state.GetAnswer(1).SelectedOptions.Count == 0;
        var isHouse = p.IsHouse || (unknownHousing && p.AnnualKwh > 3000);

        SolarMode mode;
        switch (p.Solar)
        {
            case SolarStatus.Interested:
                mode = isHouse ? SolarMode.NewPlant : SolarMode.BalconyOrPark;
                break;
            case SolarStatus.Own:
                mode = p.Battery == BatteryStatus.Has ? SolarMode.HasBattery : SolarMode.AddBattery;
                break;
            default:
                return null;
        }

        // ── Hourly load ──
        var analysis = state.Analysis is { Success: true } a ? a : null;
        double existingKwp = 0;
        double[] load;
        var fromFile = analysis?.ImportMatrix is not null;

        if (analysis?.ImportMatrix is { } importMatrix && analysis.ExportMatrix is { } exportMatrix)
        {
            // Prosumer file: rebuild real consumption = import + self-consumed generation.
            var minKwp = LoadProfileBuilder.MinimumKwpForExport(exportMatrix, _o.Yield);
            existingKwp = Math.Max(p.ExistingKwp ?? minKwp * 1.3, minKwp);
            load = LoadProfileBuilder.FromProsumerMatrices(importMatrix, exportMatrix, existingKwp, _o.Yield);
        }
        else if (analysis?.ImportMatrix is { } matrix)
        {
            load = LoadProfileBuilder.FromMatrix(matrix);
            if (mode is SolarMode.AddBattery or SolarMode.HasBattery)
            {
                // Import-only file of an owner: add the self-consumed part of the modelled generation
                // (~30 % is typical for a plant without a battery).
                existingKwp = p.ExistingKwp ?? 6;
                var kwp = existingKwp;
                load = load.Select((x, i) => x + SolarSimulator.GenShape[i] * _o.Yield * kwp * 0.3).ToArray();
            }
        }
        else
        {
            load = SyntheticLoad(p);
            if (mode is SolarMode.AddBattery or SolarMode.HasBattery)
            {
                existingKwp = p.ExistingKwp ?? 6;
            }
        }

        var scheme = p.Scheme ?? _o.DefaultScheme;

        // ── Options ──
        var yieldPerKwp = mode == SolarMode.BalconyOrPark ? _o.Yield * _o.BalconyYieldFactor : _o.Yield;
        IReadOnlyList<double> pvOptions = mode == SolarMode.BalconyOrPark ? _o.FlatKwpOptions : _o.HouseKwpOptions;
        IReadOnlyList<double> battOptions = mode switch
        {
            SolarMode.BalconyOrPark => _o.FlatBatteryOptions,
            SolarMode.HasBattery => new[] { 0, _o.AssumedExistingBatteryKwh },
            _ => _o.HouseBatteryOptions
        };

        // "What if" from the user / model.
        if (pvKwp is { } wantPv && mode is SolarMode.NewPlant or SolarMode.BalconyOrPark)
        {
            pvOptions = new[] { Math.Clamp(wantPv, 0.2, 30) };
        }

        if (batteryKwh is { } wantBatt)
        {
            var b = Math.Clamp(wantBatt, 0, 30);
            battOptions = b > 0 ? new[] { 0, b } : new[] { 0.0 };
        }

        // ── Scan for every plan type (with that type's cheapest supplier) ──
        var legacyBest = PlanCalculator.BestPerType(_calculator.Estimate(p));
        var mainType = state.Recommendation?.PlanType ?? legacyBest[0].Type;
        var byType = new Dictionary<PlanType, SolarScan>();
        var suppliers = new Dictionary<PlanType, SupplierTariff>();

        foreach (var type in Enum.GetValues<PlanType>())
        {
            var supplierName = legacyBest.First(e => e.Type == type).Supplier;
            var supplier = _calculator.Tariffs.Suppliers.First(s => s.Name == supplierName);
            suppliers[type] = supplier;

            var sim = SimFor(type, mode, p, yieldPerKwp);
            byType[type] = SolarSimulator.Analyze(load, pvOptions, battOptions,
                r => _calculator.TotalBill(r, type, supplier, scheme, p),
                _o, sim, _o.PvCostPerKwp, existingKwp);
        }

        var scan = byType[mainType];
        var chosen = mode switch
        {
            SolarMode.AddBattery or SolarMode.HasBattery => scan.BestWithBattery ?? scan.BestOverall,
            _ => scan.BestOverall
        };

        var mainSim = SimFor(mainType, mode, p, yieldPerKwp);
        var before = SolarSimulator.Simulate(load, existingKwp, 0, mainSim);
        var after = SolarSimulator.Simulate(load, chosen.PvKwp, chosen.BattKwh, mainSim);

        // Apartments may also buy a remote park share.
        SolarScan? park = null;
        if (mode == SolarMode.BalconyOrPark && pvKwp is null)
        {
            park = SolarSimulator.Analyze(load, _o.RemoteParkKwOptions, new[] { 0.0 },
                r => _calculator.TotalBill(r, mainType, suppliers[mainType], ProsumerScheme.NetMetering, p),
                _o, BaseSim() with { RemoteOnly = true }, _o.RemoteParkCostPerKw);
        }

        var worthIt = mode switch
        {
            SolarMode.AddBattery => scan.BatteryWorthIt,
            SolarMode.HasBattery => chosen.BatteryExtraYear1 > 0,
            _ => scan.SolarWorthIt
        };

        return new SolarReport
        {
            Mode = mode,
            Scheme = scheme,
            ExistingKwp = existingKwp,
            LoadFromFile = fromFile,
            PlanType = mainType,
            Supplier = suppliers[mainType].Name,
            Scan = scan,
            ByType = byType,
            Chosen = chosen,
            WorthIt = worthIt,
            Verdict = Verdict(mode, scheme, scan, chosen, byType),
            Baseline = PlanCalculator.BestPerType(_calculator.EstimateFromMatrix(before.Import288, before.Export288, scheme, p, fullBill: true)),
            WithSolar = PlanCalculator.BestPerType(_calculator.EstimateFromMatrix(after.Import288, after.Export288, scheme, p, fullBill: true)),
            MonthlyBillBefore = _calculator.MonthlyBill(before.Import288, before.Export288, mainType, suppliers[mainType], scheme, p),
            MonthlyBillAfter = _calculator.MonthlyBill(after.Import288, after.Export288, mainType, suppliers[mainType], scheme, p),
            DistributionSavedPerYear = Math.Round(Math.Max(0, before.Import - after.Import) * _o.DistributionPrice),
            Park = park,
            Assumptions = $"DEMO prielaidos: elektrinė {_o.PvCostPerKwp:0} €/kWp, kaupiklis {_o.BatteryCostPerKwh:0} €/kWh, " +
                          $"ESO skirstymas {_o.DistributionPrice:0.000} €/kWh, kaupimo tinkle mokestis {_o.NetMeteringFee:0.000} €/kWh, " +
                          $"perteklius {_o.ExportPrice:0.00} €/kWh, kainų augimas {_o.PriceGrowth:P0}/m., {_o.Horizon} m., be APVA kompensacijų."
        };
    }

    private SimOptions BaseSim() => new(_o.Yield, _o.BatteryEff, _o.Dod, _o.CRate);

    private SimOptions SimFor(PlanType type, SolarMode mode, HouseholdProfile p, double yieldPerKwp)
    {
        // Night grid charging pays only with time-varying prices; an existing battery does it only
        // when it is controlled by price, a hypothetical one is assumed to be smart.
        var gridCharge = type != PlanType.Fixed && (mode != SolarMode.HasBattery || p.BatteryPriceControlled);
        return new SimOptions(yieldPerKwp, _o.BatteryEff, _o.Dod, _o.CRate) { GridChargeAtNight = gridCharge };
    }

    private static double[] SyntheticLoad(HouseholdProfile p)
    {
        var baseKwh = Math.Max(500, p.AnnualKwh - (p.EvNightOnly ? p.EvKwh : 0));
        var load = LoadProfileBuilder.Synthetic(baseKwh, LoadProfileBuilder.ParseHours(p.PeakHours),
            winterBoost: p.ElectricHeating ? 0.5 : 0.15);

        if (p.EvNightOnly && p.EvKwh > 0)
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

    private static string Verdict(SolarMode mode, ProsumerScheme scheme, SolarScan scan, SolarOption chosen, IReadOnlyDictionary<PlanType, SolarScan> byType)
    {
        string Payback(SolarOption o) => o.PaybackYears is { } y ? $"atsiperka per ~{y.ToString("0.#", Lt)} m." : "per 25 m. neatsiperka";

        switch (mode)
        {
            case SolarMode.AddBattery:
            {
                var bestType = byType.Where(kv => kv.Value.BatteryWorthIt)
                    .OrderByDescending(kv => kv.Value.BestOverall.Npv)
                    .Select(kv => (PlanType?)kv.Key).FirstOrDefault();

                if (scan.BatteryWorthIt)
                {
                    return $"Kaupiklis verta: {chosen.BattKwh:0} kWh {Payback(chosen)}.";
                }

                if (bestType is { } t)
                {
                    return $"Su dabartiniu planu kaupiklis neatsiperka, bet su planu „{Recommendation.PlanTypeLabel(t)}“ – jau verta svarstyti.";
                }

                return scheme == ProsumerScheme.NetMetering
                    ? "Kaupiklis neatsiperka – kaupiant tinkle, tinklas jums jau atlieka „baterijos“ vaidmenį."
                    : $"Kaupiklis neatsiperka – sutaupytų tik ~{chosen.BatteryExtraYear1.ToString("0", Lt)} € per metus, o kainuotų ~{chosen.Capex.ToString("0", Lt)} €.";
            }
            case SolarMode.HasBattery:
                return chosen.BatteryExtraYear1 > 0
                    ? $"Jūsų kaupiklis sutaupo ~{chosen.BatteryExtraYear1.ToString("0", Lt)} € per metus."
                    : "Jūsų kaupiklis su šiuo planu beveik nieko nesutaupo – verta rinktis biržos planą ir valdyti jį pagal kainą.";
            default:
                if (!scan.SolarWorthIt)
                {
                    return "Pagal šias prielaidas saulės elektrinė per 25 m. neatsiperka.";
                }

                return scan.BatteryWorthIt
                    ? $"Verta saulės elektrinė IR kaupiklis: {chosen.PvKwp:0.#} kWp + {chosen.BattKwh:0} kWh {Payback(chosen)}."
                    : $"Verta saulės elektrinė ({chosen.PvKwp:0.#} kWp), {Payback(chosen)}; kaupiklis neatsiperka.";
        }
    }

    /// <summary>Compact text for the language model (≤ ~15 lines, all numbers precomputed).</summary>
    public string Describe(SolarReport r)
    {
        static string Eur(double v) => v.ToString("0", Lt) + " €";
        static string Opt(SolarOption o) =>
            $"{o.PvKwp:0.#} kWp + {o.BattKwh:0.#} kWh: investicija {Eur(o.Capex)}, 1 m. sutaupymas {Eur(o.Year1Savings)}, " +
            $"atsipirkimas {(o.PaybackYears is { } y ? y.ToString("0.#", Lt) + " m." : "neatsiperka")}, NPV {Eur(o.Npv)}, savarankiškumas {o.SelfSufficiencyPct:0} %";

        var sb = new StringBuilder();
        sb.AppendLine($"SAULĖ ({r.Mode}; schema {r.Scheme}; vertinta su planu {r.PlanType}, {r.Supplier}; duomenys {(r.LoadFromFile ? "iš ESO failo" : "įvertinti")}{(r.ExistingKwp > 0 ? $"; esama elektrinė ~{r.ExistingKwp:0.#} kWp" : "")}):");
        sb.AppendLine($"- VERDIKTAS: {r.Verdict}");
        sb.AppendLine($"- Pasirinkta: {r.ChosenLabel}. {Opt(r.Chosen)}");
        if (r.Mode is SolarMode.NewPlant or SolarMode.BalconyOrPark)
        {
            sb.AppendLine($"- Be kaupiklio geriausia: {Opt(r.Scan.BestPvOnly)}");
        }

        if (r.Scan.BestWithBattery is { } wb && wb != r.Chosen)
        {
            sb.AppendLine($"- Su kaupikliu geriausia: {Opt(wb)}");
        }

        sb.AppendLine("- Kaupiklis pagal plano tipą (NPV su kaupikliu vs be): " + string.Join("; ", r.ByType.Select(kv =>
            $"{kv.Key}: {(kv.Value.BestWithBattery is { } b ? Eur(b.Npv) : "—")} vs {Eur(kv.Value.BestPvOnly.Npv)}")));

        sb.AppendLine("- Visa sąskaita su ESO skirstymu po pakeitimo: " + string.Join("; ", r.WithSolar.Select(e =>
        {
            var before = r.Baseline.FirstOrDefault(b => b.Type == e.Type);
            return $"{e.Type} {Eur(e.MonthlyCost)}/mėn. ({Eur(e.YearlyCost)}/m.){(before is null ? "" : $", buvo {Eur(before.MonthlyCost)}/mėn.")}";
        })));
        sb.AppendLine($"- Sutaupytas ESO skirstymas ~{Eur(r.DistributionSavedPerYear)}/m. (pagrindiniai planų skaičiai yra BE skirstymo – nemaišyk jų su šiais).");

        if (r.Park is { } park)
        {
            sb.AppendLine($"- Alternatyva bute – nutolusio parko dalis: {Opt(park.BestOverall)}");
        }

        sb.AppendLine($"- {r.Assumptions} Valandinis profilis suvidurkintas – kaupiklio nauda gali būti šiek tiek pervertinta.");
        return sb.ToString();
    }

    /// <summary>Short Markdown paragraph for the rule-based recommendation.</summary>
    public string Paragraph(SolarReport r)
    {
        var o = r.Chosen;
        var title = r.Mode is SolarMode.AddBattery or SolarMode.HasBattery ? "Kaupiklis" : "Saulės elektrinė";
        var sb = new StringBuilder();
        sb.AppendLine($"**{title}:** {r.Verdict}");
        if (r.Mode != SolarMode.HasBattery)
        {
            sb.AppendLine($"- {r.ChosenLabel}: investicija ~{o.Capex.ToString("0", Lt)} €, pirmais metais sutaupytumėte ~{o.Year1Savings.ToString("0", Lt)} €.");
        }

        sb.AppendLine($"- Vidutinė sąskaita: ~{(r.MonthlyBillAfter.Sum() / 12).ToString("0", Lt)} €/mėn. vietoj ~{(r.MonthlyBillBefore.Sum() / 12).ToString("0", Lt)} €/mėn. (su ESO skirstymu).");
        return sb.ToString();
    }
}
