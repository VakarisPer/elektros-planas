namespace vamos.Services;

/// <summary>Result of running a home battery without a solar plant.</summary>
public sealed class ArbitrageResult
{
    /// <summary>New grid import of every hour (load − discharged + charged).</summary>
    public required double[] ImportSeries { get; init; }

    /// <summary>kWh delivered from the battery to the home per year.</summary>
    public double DeliveredKwh { get; init; }

    /// <summary>kWh bought from the grid to charge the battery per year.</summary>
    public double ChargedKwh { get; init; }

    /// <summary>Σ moved × (expensive − cheap ÷ η) per year, EUR – the documentation's "nauda".</summary>
    public double Benefit { get; init; }
}

/// <summary>
/// "Kaupiklis be elektrinės": every day the battery is charged from the grid in the cheapest hours and
/// discharged into the home's own consumption in the most expensive ones, only when
/// <c>brangi − pigi ÷ η &gt; 0</c>. Without permitted generation power (LGG) nothing goes back to the grid.
/// </summary>
public static class BatteryArbitrage
{
    /// <param name="load">Hourly consumption, kWh.</param>
    /// <param name="price">Marginal price of every hour for the chosen plan, EUR/kWh.</param>
    /// <param name="capacityKwh">Usable capacity (talpa), kWh.</param>
    /// <param name="powerKw">Charge / discharge power (galia), kW.</param>
    /// <param name="eta">Efficiency η – share of energy left after charging.</param>
    /// <param name="minGain">Smallest gain per delivered kWh worth cycling for, EUR.</param>
    public static ArbitrageResult Run(double[] load, double[] price, double capacityKwh, double powerKw, double eta, double minGain = 0.005)
    {
        var import = (double[])load.Clone();
        double delivered = 0, charged = 0, benefit = 0;
        if (capacityKwh <= 0 || powerKw <= 0)
        {
            return new ArbitrageResult { ImportSeries = import };
        }

        var days = load.Length / 24;
        var pairs = new List<(int Charge, int Discharge, double Gain)>(300);
        var chargeLeft = new double[24];
        var dischargeLeft = new double[24];

        for (var d = 0; d < days; d++)
        {
            var o = d * 24;
            pairs.Clear();
            for (var c = 0; c < 24; c++)
            {
                for (var h = c + 1; h < 24; h++)
                {
                    // Charge must come before discharge within the same day.
                    var gain = price[o + h] - price[o + c] / eta;
                    if (gain > minGain && load[o + h] > 0)
                    {
                        pairs.Add((c, h, gain));
                    }
                }
            }

            if (pairs.Count == 0)
            {
                continue;
            }

            pairs.Sort((a, b) => b.Gain.CompareTo(a.Gain));
            for (var h = 0; h < 24; h++)
            {
                chargeLeft[h] = powerKw;
                dischargeLeft[h] = Math.Min(powerKw, load[o + h]);
            }

            // Energy stored during the day never exceeds the capacity, so SoC stays within 0..talpa.
            var capacityLeft = capacityKwh;
            foreach (var (c, h, gain) in pairs)
            {
                if (capacityLeft <= 1e-9)
                {
                    break;
                }

                // x = kWh delivered at h; it needs x / η from the grid at c and stores x (SoC += k·η).
                var x = Math.Min(Math.Min(dischargeLeft[h], chargeLeft[c] * eta), capacityLeft);
                if (x <= 1e-9)
                {
                    continue;
                }

                dischargeLeft[h] -= x;
                chargeLeft[c] -= x / eta;
                capacityLeft -= x;

                import[o + h] -= x;
                import[o + c] += x / eta;
                delivered += x;
                charged += x / eta;
                benefit += x * gain;
            }
        }

        return new ArbitrageResult
        {
            ImportSeries = import,
            DeliveredKwh = delivered,
            ChargedKwh = charged,
            Benefit = benefit
        };
    }
}
