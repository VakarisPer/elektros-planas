namespace vamos.Models;

/// <summary>
/// Result of ESO "Saulės elektrinės prijungimo galimybės" check for one object.
/// </summary>
public sealed class EsoConnectionInfo
{
    public string ObjectNumber { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>ESO error message (e.g. invalid object number).</summary>
    public string? Error { get; set; }

    /// <summary>LOG – permitted consumption power of the object, kW.</summary>
    public double? AllowedConsumptionKw { get; set; }

    /// <summary>IGG – installed generation power already connected, kW.</summary>
    public double? InstalledGenerationKw { get; set; }

    /// <summary>LGG – permitted generation power, kW.</summary>
    public double? AllowedGenerationKw { get; set; }

    /// <summary>Preliminary assessment text written by ESO.</summary>
    public string? Message { get; set; }

    /// <summary>Export limit mentioned in <see cref="Message"/> (e.g. "ribojamas iki 1 kW"), kW.</summary>
    public double? ExportLimitKw { get; set; }

    public DateTime CheckedAt { get; set; }

    public bool HasPlant => InstalledGenerationKw is > 0;

    /// <summary>
    /// Power that can be connected for a new plant ("galima apkrova"): the export limit ESO mentions,
    /// otherwise no restriction is assumed (null).
    /// </summary>
    public double? AvailableCapacityKw => Success ? ExportLimitKw : null;
}
