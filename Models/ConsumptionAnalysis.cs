namespace vamos.Models;

/// <summary>
/// Result of analysing an uploaded electricity consumption file.
/// </summary>
public sealed class ConsumptionAnalysis
{
    public bool Success { get; set; }

    public string? Error { get; set; }

    public string? FileName { get; set; }

    public double TotalKwh { get; set; }

    public int Months { get; set; }

    public int Readings { get; set; }

    public double? MonthlyAverageKwh { get; set; }

    /// <summary>Share of consumption during night hours (22:00–06:00), 0..1.</summary>
    public double? NightShare { get; set; }

    /// <summary>Peak period mapped to a Q15 option label, when hourly data is available.</summary>
    public string? PeakPeriodLabel { get; set; }

    /// <summary>
    /// Average monthly energy given back to the grid (ESO "P-", own solar plant), kWh.
    /// When set, <see cref="MonthlyAverageKwh"/> is grid import only, not total consumption.
    /// </summary>
    public double? ExportedMonthlyKwh { get; set; }

    /// <summary>Average grid import per hour, kWh, indexed month × 24 + hour (hourly files only).</summary>
    public double[]? ImportMatrix { get; set; }

    /// <summary>Average export to the grid per hour, kWh, indexed month × 24 + hour (prosumers only).</summary>
    public double[]? ExportMatrix { get; set; }
}
