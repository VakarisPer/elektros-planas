namespace vamos.Services;

/// <summary>OpenRouter connection settings (section "OpenRouter"). The key lives in user-secrets.</summary>
public sealed class OpenRouterOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash";

    public string? ApiKey { get; set; }

    /// <summary>Hard limit of questions the advisor may ask.</summary>
    public int MaxQuestions { get; set; } = 14;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Exchange price scenarios (section "Tariffs"). Grid and supplier prices live in Data/Tariffs,
/// hourly Nord Pool prices in Data/Prices/np-lt.csv.
/// </summary>
public sealed class TariffOptions
{
    /// <summary>Spot price multiplier for an expensive year (e.g. 1.6 = +60 %).</summary>
    public double SpotStressFactor { get; set; } = 1.6;

    /// <summary>Spot price multiplier for a cheap year.</summary>
    public double SpotCalmFactor { get; set; } = 0.8;

    /// <summary>LG assumed when ESO data is missing, kW.</summary>
    public double DefaultFlatLgKw { get; set; } = 5;

    public double DefaultHouseLgKw { get; set; } = 12;
}
