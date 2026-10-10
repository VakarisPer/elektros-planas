namespace vamos.Services;

/// <summary>Physical parameters of one simulation run.</summary>
public sealed record SimOptions(double YieldPerKwp, double Eff, double Dod, double CRate)
{
    /// <summary>
    /// Simple price arbitrage for exchange / two-zone plans: in months without solar surplus the
    /// battery is charged from the grid at night (0–6) up to the expected evening (17–22) deficit.
    /// </summary>
    public bool GridChargeAtNight { get; init; }

    /// <summary>Remote park share: nothing is self-consumed, all generation goes to the grid.</summary>
    public bool RemoteOnly { get; init; }
}

/// <summary>Yearly energy flows of one configuration; matrices are kWh per (month × 24 + hour) cell.</summary>
public sealed class SimResult
{
    public double Load { get; init; }
    public double Import { get; init; }
    public double Export { get; init; }
    public double Generation { get; init; }
    public double[] Import288 { get; init; } = new double[288];
    public double[] Export288 { get; init; } = new double[288];

    public double SelfConsumptionPct => Generation > 0 ? 100 * (Generation - Export) / Generation : 0;
    public double SelfSufficiencyPct => Load > 0 ? 100 * (1 - Import / Load) : 0;
}

/// <summary>Economics of one PV + battery configuration (mirrors solar_calc.py <c>evaluate</c>).</summary>
public sealed class SolarOption
{
    public double PvKwp { get; init; }
    public double BattKwh { get; init; }
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
/// C# port of <c>tools/solar_calc.py</c>: hourly PV generation for Vilnius, battery dispatch and
/// NPV / payback. Kept as close to the Python as possible so results can be checked for parity
/// (see tools/parity); the only extensions are a pluggable cost function, an existing-plant mode,
/// night grid charging and remote park shares.
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
        var eta = Math.Sqrt(o.Eff);
        var usable = battKwh * o.Dod;
        var pmax = battKwh * o.CRate;
        var soc = 0.0;
        double imp = 0, exp = 0, gen = 0;
        var import288 = new double[288];
        var export288 = new double[288];

        // Expected evening (17–22) deficit per day, used by the night grid-charging rule.
        double[]? eveningNeed = null;
        if (o.GridChargeAtNight && battKwh > 0)
        {
            eveningNeed = new double[365];
            for (var i = 0; i < Hours; i++)
            {
                var hod = i % 24;
                if (hod is >= 17 and < 22)
                {
                    eveningNeed[i / 24] += Math.Max(0, load[i] - GenShape[i] * o.YieldPerKwp * pvKwp);
                }
            }
        }

        for (var i = 0; i < Hours; i++)
        {
            var hod = i % 24;
            var cell = MonthOf[i] * 24 + hod;
            var l = load[i];
            var g = GenShape[i] * o.YieldPerKwp * pvKwp;
            gen += g;

            double direct, surplus, deficit;
            if (o.RemoteOnly)
            {
                direct = 0;
                surplus = g;
                deficit = l;
            }
            else
            {
                direct = Math.Min(l, g);
                surplus = g - direct;
                deficit = l - direct;
            }

            var gridCharge = 0.0;
            if (battKwh > 0 && !o.RemoteOnly)
            {
                var ch = Math.Min(Math.Min(surplus, (usable - soc) / eta), pmax);
                if (ch > 0)
                {
                    soc += ch * eta;
                    surplus -= ch;
                }

                // Winter nights: fill the battery from the grid for the expensive evening.
                if (eveningNeed is not null && hod < 6 && IsLowSunMonth(MonthOf[i]))
                {
                    var target = Math.Min(usable, eveningNeed[i / 24] / eta);
                    gridCharge = Math.Max(0, Math.Min(Math.Min((target - soc) / eta, (usable - soc) / eta), pmax));
                    soc += gridCharge * eta;
                }

                var dis = Math.Min(Math.Min(deficit, soc * eta), pmax);
                if (dis > 0)
                {
                    soc -= dis / eta;
                    deficit -= dis;
                }
            }

            var hourImport = deficit + gridCharge;
            imp += hourImport;
            exp += surplus;
            import288[cell] += hourImport;
            export288[cell] += surplus;
        }

        return new SimResult
        {
            Load = load.Sum(),
            Import = imp,
            Export = exp,
            Generation = gen,
            Import288 = import288,
            Export288 = export288
        };
    }

    private static bool IsLowSunMonth(int month) => month is <= 1 or >= 9;

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
            var capexNet = capex * (1 - p.SubsidyPct / 100);

            var cash = new List<double> { -capexNet };
            for (var y = 1; y <= p.Horizon; y++)
            {
                var pf = Math.Pow(1 + p.PriceGrowth, y - 1);
                var s = savPv * Math.Pow(1 - p.PvDegradation, y - 1) * pf;
                if (batt > 0 && y <= p.BatteryLife)
                {
                    s += battExtra * pf * Math.Pow(1 - p.BatteryFade, y - 1);
                }

                if (y == p.InverterYear && newPv > 0 && !sim.RemoteOnly)
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

            var r = Run(pv, batt);
            return new SolarOption
            {
                PvKwp = pv,
                BattKwh = batt,
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
}
