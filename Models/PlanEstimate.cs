namespace vamos.Models;

/// <summary>Plan family compared by the calculator.</summary>
public enum PlanType
{
    Fixed,
    Exchange,
    TwoZone
}

/// <summary>
/// Estimated yearly energy cost (supplier part only, without ESO distribution) of one plan.
/// </summary>
public sealed class PlanEstimate
{
    public PlanType Type { get; set; }

    public string Supplier { get; set; } = string.Empty;

    public string PlanName { get; set; } = string.Empty;

    /// <summary>Expected yearly cost, EUR.</summary>
    public double YearlyCost { get; set; }

    /// <summary>Average monthly cost, EUR.</summary>
    public double MonthlyCost => YearlyCost / 12;

    /// <summary>Expensive year (exchange price spike), EUR.</summary>
    public double WorstCost { get; set; }

    /// <summary>Cheap year with full use of flexibility, EUR.</summary>
    public double BestCost { get; set; }

    /// <summary>Effective price, EUR/kWh.</summary>
    public double EffectivePrice { get; set; }

    /// <summary>Cost adjusted by the user's risk aversion; lower is better.</summary>
    public double Score { get; set; }

    /// <summary>"Žema", "Vidutinė" or "Aukšta".</summary>
    public string RiskLevel { get; set; } = string.Empty;
}

/// <summary>Household facts derived from the answers; inputs of the calculator.</summary>
public sealed class HouseholdProfile
{
    /// <summary>Total yearly consumption, kWh.</summary>
    public double AnnualKwh { get; set; }

    /// <summary>Energy bought from the grid after own generation, kWh.</summary>
    public double PurchasedKwh { get; set; }

    public double NightShare { get; set; }

    public double EveningShare { get; set; }

    public double DayShare => Math.Max(0, 1 - NightShare - EveningShare);

    /// <summary>Share of consumption that can realistically be moved to cheap hours, 0..0.45.</summary>
    public double ShiftPotential { get; set; }

    /// <summary>0 = loves risk, 1 = hates risk.</summary>
    public double RiskAversion { get; set; }

    /// <summary>Extra winter weight on purchased energy (heating, solar users), 1 = none.</summary>
    public double WinterWeight { get; set; } = 1;

    public bool HasSmartMeter { get; set; }

    public bool HasSolar { get; set; }

    public bool HasBattery { get; set; }

    public bool IsHouse { get; set; }

    public SolarStatus Solar { get; set; }

    /// <summary>Plant size from Q26 (midpoint), when known.</summary>
    public double? ExistingKwp { get; set; }

    public BatteryStatus Battery { get; set; }

    public bool BatteryPriceControlled { get; set; }

    /// <summary>Settlement scheme from Q27; null when unknown.</summary>
    public vamos.Services.ProsumerScheme? Scheme { get; set; }

    /// <summary>Peak hours for the synthetic load profile, e.g. "17-22".</summary>
    public string PeakHours { get; set; } = "18-22";

    /// <summary>Yearly kWh charged by an EV at home.</summary>
    public double EvKwh { get; set; }

    public bool EvNightOnly { get; set; }

    /// <summary>Heat pump or electric heating – consumption strongly peaks in winter.</summary>
    public bool ElectricHeating { get; set; }

    public bool ConsumptionFromFile { get; set; }

    public List<string> Notes { get; set; } = new();
}

public enum SolarStatus
{
    None,
    Interested,
    Own,
    Remote
}

public enum BatteryStatus
{
    Unknown,
    Has,
    Considering,
    No
}

/// <summary>Final advice stored after the advisor (AI or rules) finishes.</summary>
public sealed class Recommendation
{
    public PlanType PlanType { get; set; }

    public string Supplier { get; set; } = string.Empty;

    /// <summary>Markdown explanation (rekomendacija, kodėl, rizikos, patarimai).</summary>
    public string SummaryMarkdown { get; set; } = string.Empty;

    /// <summary>"Aukštas", "Vidutinis" or "Žemas".</summary>
    public string Confidence { get; set; } = string.Empty;

    /// <summary>True when written by the language model, false for the rule-based fallback.</summary>
    public bool ByAi { get; set; }

    public static string PlanTypeLabel(PlanType type) => type switch
    {
        PlanType.Fixed => "Fiksuota kaina",
        PlanType.Exchange => "Biržos (dinaminė) kaina",
        PlanType.TwoZone => "Dviejų laiko zonų",
        _ => type.ToString()
    };
}

/// <summary>A message shown in the assistant chat.</summary>
public sealed class ChatEntry
{
    public bool FromUser { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>When set, the bubble shows this question with selectable options.</summary>
    public int? QuestionId { get; set; }
}
