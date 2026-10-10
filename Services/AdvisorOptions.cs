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
/// Indicative supplier tariffs (section "Tariffs"). Demo values – update from supplier websites.
/// All prices EUR/kWh with VAT, energy component only (ESO distribution is the same for every supplier).
/// </summary>
public sealed class TariffOptions
{
    /// <summary>Average Nord Pool LT day-ahead price with VAT, EUR/kWh.</summary>
    public double AverageSpotPrice { get; set; } = 0.11;

    /// <summary>Exchange price multiplier for an expensive year (e.g. 1.6 = +60 %).</summary>
    public double SpotStressFactor { get; set; } = 1.6;

    /// <summary>Exchange price multiplier for a cheap year.</summary>
    public double SpotCalmFactor { get; set; } = 0.8;

    /// <summary>Relative spot price by period (night 23–7, day 7–17, evening 17–23).</summary>
    public double NightFactor { get; set; } = 0.7;

    public double DayFactor { get; set; } = 0.95;

    public double EveningFactor { get; set; } = 1.35;

    /// <summary>Relative spot price per month (Jan..Dec, mean ≈ 1): winter is expensive, summer cheap.</summary>
    public double[] MonthlySpotFactors { get; set; } = { 1.35, 1.3, 1.1, 0.9, 0.8, 0.8, 0.85, 0.85, 0.95, 1.05, 1.2, 1.35 };

    public List<SupplierTariff> Suppliers { get; set; } = new();
}

public sealed class SupplierTariff
{
    public string Name { get; set; } = string.Empty;

    public string FixedPlanName { get; set; } = "Fiksuotas";

    public double FixedPrice { get; set; }

    public string ExchangePlanName { get; set; } = "Biržos";

    /// <summary>Supplier margin on top of the spot price, EUR/kWh.</summary>
    public double ExchangeMargin { get; set; }

    public string TwoZonePlanName { get; set; } = "Dviejų zonų";

    public double TwoZoneDayPrice { get; set; }

    public double TwoZoneNightPrice { get; set; }

    /// <summary>Monthly subscription fee, EUR.</summary>
    public double MonthlyFee { get; set; }
}
