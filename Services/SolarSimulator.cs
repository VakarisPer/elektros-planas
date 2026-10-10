namespace vamos.Services;

/// <summary>
/// Physical parameters of one simulation run. <paramref name="Eff"/> is the battery efficiency η
/// (applied when charging), <paramref name="CRate"/> sets the battery power: galia = talpa × CRate.
/// </summary>
public sealed record SimOptions(double YieldPerKwp, double Eff, double CRate)
{
    /// <summary>Grid export cap set by ESO (e.g. 1 kW); surplus above it is curtailed and lost.</summary>
    public double? ExportLimitKw { get; init; }
}

/// <summary>Yearly energy flows of one configuration, with the hourly import / export series.</summary>
public sealed class SimResult
{
    public double Load { get; init; }
    public double Import { get; init; }
    public double Export { get; init; }
    public double Generation { get; init; }

    /// <summary>Plant size of this run, kWp.</summary>
    public double PvKwp { get; init; }

    /// <summary>Battery size of this run, kWh.</summary>
    public double BattKwh { get; init; }

    /// <summary>kWh bought from the grid in every hour of <see cref="TariffCalendar"/>.</summary>
    public double[] ImportSeries { get; init; } = new double[SolarSimulator.Hours];

    /// <summary>kWh given to the grid in every hour.</summary>
    public double[] ExportSeries { get; init; } = new double[SolarSimulator.Hours];

    public double SelfConsumptionPct => Generation > 0 ? 100 * (Generation - Export) / Generation : 0;
    public double SelfSufficiencyPct => Load > 0 ? 100 * (1 - Import / Load) : 0;
}

/// <summary>Economics of one PV + battery configuration (mirrors solar_calc.py <c>evaluate</c>).</summary>
public sealed class SolarOption
{
    public double PvKwp { get; init; }
    public double BattKwh { get; init; }

    /// <summary>Approximate equipment price before APVA, EUR.</summary>
    public double GrossCapex { get; init; }

    /// <summary>APVA compensation, EUR.</summary>
    public double Subsidy { get; init; }

    /// <summary>Final investment after APVA, EUR.</summary>
    public double Capex { get; init; }
    public double Year1Savings { get; init; }
    public double BatteryExtraYear1 { get; init; }
    public double? PaybackYears { get; init; }
    public double Npv { get; init; }
    public double Generation { get; init; }
    public double SelfConsumptionPct { get; init; }
    public double SelfSufficiencyPct { get; init; }
}

/// <summary>Result of scanning all configurations (mirrors solar_calc.py <c>analyze</c>).</summary>
public sealed class SolarScan
{
    public required SolarOption BestOverall { get; init; }
    public required SolarOption BestPvOnly { get; init; }
    public SolarOption? BestWithBattery { get; init; }
    public bool SolarWorthIt { get; init; }
    public bool BatteryWorthIt { get; init; }
    public required IReadOnlyList<SolarOption> All { get; init; }
}

/// <summary>
/// Hourly PV generation for Vilnius and NPV / payback ported from <c>tools/solar_calc.py</c>
/// (PV-only results are checked for parity, see tools/parity). The battery follows the project
/// documentation instead: <c>k = min(p, galia·Δt, (talpa − SoC)/η)</c>, <c>SoC += k·η</c> when
/// charging from surplus, <c>d = min(−p, galia·Δt, SoC)</c> when covering a deficit. The battery
/// never discharges into the grid.
/// </summary>
public static class SolarSimulator
{
    public const int Hours = 8760;
    private const double Lat = 54.7;

    private static readonly int[] MonthDays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    /// <summary>Relative clear-sky factor per month (Vilnius).</summary>
    private static readonly double[] MonthWeather = { 0.45, 0.60, 0.75, 0.85, 0.90, 0.90, 0.90, 0.90, 0.80, 0.65, 0.45, 0.40 };

    /// <summary>Month index (0–11) of every hour of the year.</summary>
    public static readonly int[] MonthOf = BuildMonthOf();

    /// <summary>Normalised hourly generation shape (sum = 1).</summary>
    public static readonly double[] GenShape = BuildGenShape();

    private static int[] BuildMonthOf()
    {
        var result = new int[Hours];
        var i = 0;
        for (var m = 0; m < 12; m++)
        {
            for (var h = 0; h < MonthDays[m] * 24; h++)
            {
                result[i++] = m;
            }
        }

        return result;
    }

    private static double[] BuildGenShape()
    {
        var lat = Lat * Math.PI / 180;
        var shape = new double[Hours];
        for (var i = 0; i < Hours; i++)
        {
            var n = i / 24 + 1;
            var hr = i % 24 + 0.5;
            var noon = 12.3 + (n is >= 90 and <= 300 ? 1.0 : 0.0); // daylight saving time
            var dec = 23.45 * Math.PI / 180 * Math.Sin(2 * Math.PI * (284 + n) / 365);
            var ha = 15 * (hr - noon) * Math.PI / 180;
            var s = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(ha);
            shape[i] = Math.Max(0, s) * MonthWeather[MonthOf[i]];
        }

        var total = shape.Sum();
        for (var i = 0; i < Hours; i++)
        {
            shape[i] /= total;
        }

        return shape;
    }

    /// <summary>Average generation per kWp in each (month × 24 + hour) cell, kWh per hour.</summary>
    public static double[] GenerationMatrixPerKwp(double yieldPerKwp)
    {
        var sum = new double[288];
        var count = new int[288];
        for (var i = 0; i < Hours; i++)
        {
            var cell = MonthOf[i] * 24 + i % 24;
            sum[cell] += GenShape[i] * yieldPerKwp;
            count[cell]++;
        }

        for (var c = 0; c < 288; c++)
        {
            sum[c] /= Math.Max(1, count[c]);
        }

        return sum;
    }

    public static SimResult Simulate(double[] load, double pvKwp, double battKwh, SimOptions o)
    {
        var eta = o.Eff;
        var power = battKwh * o.CRate; // kW, Δt = 1 h
        var soc = 0.0;
        double imp = 0, exp = 0, gen = 0;
        var importSeries = new double[Hours];
        var exportSeries = new double[Hours];

        for (var i = 0; i < Hours; i++)
        {
            var l = load[i];
            var g = GenShape[i] * o.YieldPerKwp * pvKwp;
            gen += g;

            var p = g - l;
            var surplus = Math.Max(0, p);
            var deficit = Math.Max(0, -p);

            if (battKwh > 0 && p > 0)
            {
                var k = Math.Min(Math.Min(p, power), (battKwh - soc) / eta);
                soc += k * eta;
                surplus = p - k;
            }
            else if (battKwh > 0 && p < 0)
            {
                var d = Math.Min(Math.Min(-p, power), soc);
                soc -= d;
                deficit = -p - d;
            }

            if (o.ExportLimitKw is { } limit && surplus > limit)
            {
                surplus = limit;
            }

            imp += deficit;
            exp += surplus;
            importSeries[i] = deficit;
            exportSeries[i] = surplus;
        }

        return new SimResult
        {
            Load = load.Sum(),
            Import = imp,
            Export = exp,
            Generation = gen,
            PvKwp = pvKwp,
            BattKwh = battKwh,
            ImportSeries = importSeries,
            ExportSeries = exportSeries
        };
    }

    /// <summary>
    /// Scans every configuration and evaluates it like solar_calc.py. <paramref name="annualCost"/>
    /// prices a simulation result (flat price = parity mode). With <paramref name="existingKwp"/> &gt; 0
    /// the plant already exists: only batteries are evaluated and the plant itself is not charged.
    /// </summary>
    public static SolarScan Analyze(
        double[] load,
        IReadOnlyList<double> pvOptions,
        IReadOnlyList<double> batteryOptions,
        Func<SimResult, double> annualCost,
        SolarOptions p,
        SimOptions sim,
        double pvCostPerKwp,
        double existingKwp = 0)
    {
        var cache = new Dictionary<(double, double), SimResult>();
        SimResult Run(double pv, double batt)
        {
            if (!cache.TryGetValue((pv, batt), out var r))
            {
                r = Simulate(load, pv, batt, sim);
                cache[(pv, batt)] = r;
            }

            return r;
        }

        var baseCost = annualCost(Run(existingKwp, 0));
        double Savings(double pv, double batt) => baseCost - annualCost(Run(pv, batt));

        var pvList = existingKwp > 0 ? new[] { existingKwp } : pvOptions.ToArray();
        var results = new List<SolarOption>();
        foreach (var pv in pvList)
        {
            foreach (var batt in batteryOptions)
            {
                results.Add(Evaluate(pv, batt));
            }
        }

        var bestPvOnly = results.Where(r => r.BattKwh == 0).MaxBy(r => r.Npv)!;
        var bestAny = results.MaxBy(r => r.Npv)!;
        var bestWithBattery = results.Where(r => r.BattKwh > 0).MaxBy(r => r.Npv);

        return new SolarScan
        {
            BestOverall = bestAny,
            BestPvOnly = bestPvOnly,
            BestWithBattery = bestWithBattery,
            SolarWorthIt = existingKwp > 0 || bestPvOnly.Npv > 0,
            BatteryWorthIt = bestAny.BattKwh > 0 && bestAny.Npv > bestPvOnly.Npv + 100,
            All = results.OrderByDescending(r => r.Npv).ToList()
        };

        SolarOption Evaluate(double pv, double batt)
        {
            var savPv = Savings(pv, 0);
            var battExtra = batt > 0 ? Savings(pv, batt) - savPv : 0;
            var newPv = Math.Max(0, pv - existingKwp);

            var capex = newPv * pvCostPerKwp + batt * p.BatteryCostPerKwh
                        + (existingKwp > 0 && batt > 0 ? p.BatteryRetrofitExtra : 0);
            var subsidy = Apva(newPv, batt, p);
            var capexNet = capex - subsidy;
            var (npv, payback) = Cashflow(capexNet, savPv, battExtra, batt, newPv, p);

            var r = Run(pv, batt);
            return new SolarOption
            {
                PvKwp = pv,
                BattKwh = batt,
                GrossCapex = Math.Round(capex),
                Subsidy = Math.Round(subsidy),
                Capex = Math.Round(capexNet),
                Year1Savings = Math.Round(Savings(pv, batt)),
                BatteryExtraYear1 = Math.Round(battExtra),
                PaybackYears = payback is null ? null : Math.Round(payback.Value, 1),
                Npv = Math.Round(npv),
                Generation = Math.Round(r.Generation),
                SelfConsumptionPct = Math.Round(r.SelfConsumptionPct),
                SelfSufficiencyPct = Math.Round(r.SelfSufficiencyPct)
            };
        }
    }

    /// <summary>APVA compensation (DEMO rates): per kW of new PV up to a cap, per kWh of battery.</summary>
    public static double Apva(double newPvKwp, double battKwh, SolarOptions p) =>
        Math.Min(newPvKwp, p.ApvaPvMaxKw) * p.ApvaPvPerKw + Math.Min(battKwh * p.ApvaBatteryPerKwh, p.ApvaBatteryMaxEur);

    /// <summary>
    /// NPV and payback of an investment (solar_calc.py <c>evaluate</c>): yearly savings grow with
    /// prices, PV degrades, the battery part fades and ends after its life, the inverter is replaced once.
    /// </summary>
    public static (double Npv, double? Payback) Cashflow(double capexNet, double savPv, double battExtra, double batt,
        double newPv, SolarOptions p)
    {
        var cash = new List<double> { -capexNet };
        for (var y = 1; y <= p.Horizon; y++)
        {
            var pf = Math.Pow(1 + p.PriceGrowth, y - 1);
            var s = savPv * Math.Pow(1 - p.PvDegradation, y - 1) * pf;
            if (batt > 0 && y <= p.BatteryLife)
            {
                s += battExtra * pf * Math.Pow(1 - p.BatteryFade, y - 1);
            }

            if (y == p.InverterYear && newPv > 0)
            {
                s -= newPv * p.InverterCost;
            }

            cash.Add(s);
        }

        var npv = cash.Select((c, t) => c / Math.Pow(1 + p.Discount, t)).Sum();

        double? payback = null;
        double cum = 0;
        for (var t = 0; t < cash.Count; t++)
        {
            var prev = cum;
            cum += cash[t];
            if (t > 0 && cum >= 0 && prev < 0)
            {
                payback = (t - 1) + (-prev / cash[t]);
                break;
            }
        }

        return (npv, payback);
    }
}
