namespace vamos.Services;

/// <summary>Builds an hourly (8760, <see cref="TariffCalendar"/>) consumption series.</summary>
public static class LoadProfileBuilder
{
    /// <summary>
    /// Port of solar_calc.py <c>build_load</c>: yearly kWh spread over the year with heavier
    /// peak hours and a winter boost. With <paramref name="weekendDaytime"/> people are at home on
    /// weekends and holidays, so 10–17 h weigh like peak hours on those days.
    /// </summary>
    public static double[] Synthetic(double annualKwh, ISet<int> peakHours, double peakWeight = 2.5, double winterBoost = 0.15, bool weekendDaytime = false)
    {
        var w = new double[SolarSimulator.Hours];
        for (var i = 0; i < w.Length; i++)
        {
            var hod = i % 24;
            var peak = peakHours.Contains(hod) || (weekendDaytime && TariffCalendar.FreeDay[i] && hod is >= 10 and < 17);
            var @base = peak ? peakWeight : hod < 6 ? 0.6 : 1.0;
            var doy = i / 24;
            var season = 1 + winterBoost * Math.Cos(2 * Math.PI * (doy - 15) / 365);
            w[i] = @base * season;
        }

        var total = w.Sum();
        for (var i = 0; i < w.Length; i++)
        {
            w[i] = w[i] * annualKwh / total;
        }

        return w;
    }

    /// <summary>Port of solar_calc.py <c>parse_hours</c>: "7-9,17-22" → {7,8,17,…,21}, [from, to).</summary>
    public static HashSet<int> ParseHours(string spec)
    {
        var hours = new HashSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-');
            int a = int.Parse(ab[0]) % 24, b = int.Parse(ab[1]) % 24;
            for (var h = a; h % 24 != b; h++)
            {
                hours.Add(h % 24);
            }
        }

        return hours;
    }

    /// <summary>Expands a 576 matrix (month × day type × hour, average kWh per hour) into a full year.</summary>
    public static double[] FromMatrix(double[] matrix)
    {
        var load = new double[SolarSimulator.Hours];
        for (var i = 0; i < load.Length; i++)
        {
            load[i] = matrix[TariffCalendar.Cell576(i)];
        }

        return load;
    }

    /// <summary>Average generation per kWp in every 576 cell (generation does not depend on the day type).</summary>
    private static double[] Generation576(double yieldPerKwp)
    {
        var gen = SolarSimulator.GenerationMatrixPerKwp(yieldPerKwp);
        var result = new double[576];
        for (var c = 0; c < 576; c++)
        {
            var month = c / 48;
            result[c] = gen[month * 24 + c % 24];
        }

        return result;
    }

    /// <summary>
    /// Real consumption of a prosumer from ESO data: import + self-consumed generation, where
    /// self-consumption ≈ modelled generation − measured export (never negative).
    /// </summary>
    public static double[] FromProsumerMatrices(double[] import, double[] export, double kwp, double yieldPerKwp)
    {
        var gen = Generation576(yieldPerKwp);
        var matrix = new double[576];
        for (var c = 0; c < 576; c++)
        {
            matrix[c] = import[c] + Math.Max(0, gen[c] * kwp - export[c]);
        }

        return FromMatrix(matrix);
    }

    /// <summary>
    /// Smallest plant size that could have produced the measured export in every sunny cell; the
    /// real plant is at least this big.
    /// </summary>
    public static double MinimumKwpForExport(double[] export, double yieldPerKwp)
    {
        var gen = Generation576(yieldPerKwp);
        var max = 0.0;
        for (var c = 0; c < 576; c++)
        {
            // Ignore dawn/dusk cells where the model has almost no sun.
            if (gen[c] > 0.05)
            {
                max = Math.Max(max, export[c] / gen[c]);
            }
        }

        return max;
    }

    /// <summary>
    /// Moves <paramref name="share"/> of the 7–22 h consumption of every day into 0–5 h of the same
    /// day – what a flexible household does on time-varying prices (cheap night in every zoning).
    /// </summary>
    public static double[] Shift(double[] series, double share)
    {
        var result = (double[])series.Clone();
        if (share <= 0)
        {
            return result;
        }

        for (var d = 0; d < result.Length / 24; d++)
        {
            var o = d * 24;
            var moved = 0.0;
            for (var h = 7; h < 22; h++)
            {
                var amount = result[o + h] * share;
                result[o + h] -= amount;
                moved += amount;
            }

            for (var h = 0; h < 5; h++)
            {
                result[o + h] += moved / 5;
            }
        }

        return result;
    }
}
