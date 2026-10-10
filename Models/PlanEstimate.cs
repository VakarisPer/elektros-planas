namespace vamos.Models;

/// <summary>Plan family compared by the calculator.</summary>
public enum PlanType
{
    /// <summary>Fixed price, one zone.</summary>
    Fixed,

    /// <summary>Exchange price (np + margin) × VAT.</summary>
    Exchange,

    /// <summary>Fixed prices by time zone (2 or 4 zones).</summary>
    Zoned
}

/// <summary>
/// Full yearly bill of one combination: ESO plan + zones + supplier plan (+ prosumer settlement).
/// </summary>
public sealed class PlanEstimate
{
    /// <summary>Short code of the combination, e.g. "N2:ENE-F12z2" (used by the AI's finish tool).</summary>
    public string Id { get; set; } = string.Empty;

    public PlanType Type { get; set; }

    public string Supplier { get; set; } = string.Empty;

    /// <summary>Supplier plan name, e.g. "Enefit fiksuota 12 mėn., 2 zonos".</summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>ESO plan id: Standartinis, Efektyvus or Namai.</summary>
    public string EsoPlan { get; set; } = string.Empty;

    public int Zones { get; set; } = 1;

    public int DurationMonths { get; set; }

    public bool PublicSupply { get; set; }

    /// <summary>"ESO Namai, 2 zonos".</summary>
    public string EsoLabel => $"ESO {EsoPlan}, {(Zones == 1 ? "1 zona" : $"{Zones} zonos")}";

    /// <summary>Prosumer settlement used, e.g. "Dvipusė apskaita, 1 būdas"; null without own generation.</summary>
    public string? Settlement { get; set; }

    /// <summary>Bill split (ESO zones, fixed fees, energy, VIAP + DED, prosumer part).</summary>
    public vamos.Services.BillBreakdown? Breakdown { get; set; }

    /// <summary>Expected yearly cost, EUR.</summary>
    public double YearlyCost { get; set; }

    /// <summary>Average monthly cost, EUR.</summary>
    public double MonthlyCost => YearlyCost / 12;

    /// <summary>Expensive year (exchange price spike), EUR.</summary>
    public double WorstCost { get; set; }

    /// <summary>Cheap year with full use of flexibility, EUR.</summary>
    public double BestCost { get; set; }

    /// <summary>Effective full price per kWh taken from the grid, EUR/kWh.</summary>
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

    /// <summary>Energy taken from the grid per year (import), kWh.</summary>
    public double PurchasedKwh { get; set; }

    /// <summary>Share of grid import 22–6 h.</summary>
    public double NightShare { get; set; }

    /// <summary>Share of grid import 17–22 h.</summary>
    public double EveningShare { get; set; }

    public double DayShare => Math.Max(0, 1 - NightShare - EveningShare);

    /// <summary>Share of import 17–21 h – expensive on the exchange.</summary>
    public double PeakShare { get; set; }

    /// <summary>Share of import in November–February.</summary>
    public double WinterShare { get; set; }

    /// <summary>Share of import in the 2-zone night (night + weekends + holidays).</summary>
    public double TwoZoneNightShare { get; set; }

    /// <summary>Permitted consumption power LG, kW (ESO check or a typical value).</summary>
    public double LgKw { get; set; } = 5;

    public bool LgFromEso { get; set; }

    /// <summary>Permitted generation power LGG, kW (ESO check or the plant size).</summary>
    public double LggKw { get; set; }

    /// <summary>Real consumption of every hour (import + self-consumed generation), kWh.</summary>
    public double[] Consumption { get; set; } = Array.Empty<double>();

    /// <summary>Grid import of every hour, kWh.</summary>
    public double[] Import { get; set; } = Array.Empty<double>();

    /// <summary>Grid export of every hour, kWh; null without own generation.</summary>
    public double[]? Export { get; set; }

    /// <summary>Import / export come from the uploaded ESO file (not modelled).</summary>
    public bool FlowsFromFile { get; set; }

    /// <summary>Personal weighted spot price Σ(kwh·np)/Σkwh, EUR/kWh without VAT.</summary>
    public double PersonalWeightedPrice { get; set; }

    /// <summary>Plain mean spot price of the same year, EUR/kWh without VAT.</summary>
    public double MeanSpotPrice { get; set; }

    /// <summary>Share of consumption that can realistically be moved to cheap hours, 0..0.45.</summary>
    public double ShiftPotential { get; set; }

    /// <summary>0 = loves risk, 1 = hates risk.</summary>
    public double RiskAversion { get; set; }

    public bool HasSmartMeter { get; set; }

    public bool HasSolar { get; set; }

    public bool HasBattery { get; set; }

    public bool IsHouse { get; set; }

    public SolarStatus Solar { get; set; }

    /// <summary>Plant size from Q26 (midpoint), when known.</summary>
    public double? ExistingKwp { get; set; }

    public BatteryStatus Battery { get; set; }

    /// <summary>Interested house: less than 5 kW can be connected, so only a battery is possible.</summary>
    public bool LowGridCapacity { get; set; }

    /// <summary>Interested house: evaluate solar panels.</summary>
    public bool WantsPanels { get; set; }

    /// <summary>Interested house: evaluate a battery (priced with the dynamic tariff only).</summary>
    public bool WantsBattery { get; set; }

    /// <summary>Peak hours for the synthetic load profile, e.g. "17-22".</summary>
    public string PeakHours { get; set; } = "7-9,17-22";

    /// <summary>Yearly kWh charged by an EV at home.</summary>
    public double EvKwh { get; set; }


    /// <summary>Heat pump or electric heating – consumption strongly peaks in winter.</summary>
    public bool ElectricHeating { get; set; }

    public bool ConsumptionFromFile { get; set; }

    /// <summary>Where the yearly consumption comes from, e.g. "pagal ESO duomenis".</summary>
    public string ConsumptionSource { get; set; } = string.Empty;

    public List<string> Notes { get; set; } = new();
}

public enum SolarStatus
{
    None,
    Interested,
    Own
}

public enum BatteryStatus
{
    Unknown,
    Has,
    No
}

/// <summary>Final advice stored after the advisor (AI or rules) finishes.</summary>
public sealed class Recommendation
{
    public PlanType PlanType { get; set; }

    public string Supplier { get; set; } = string.Empty;

    /// <summary>Recommended combination id (<see cref="PlanEstimate.Id"/>).</summary>
    public string? PlanId { get; set; }

    /// <summary>"ESO Namai, 2 zonos + Enefit fiksuota 12 mėn., 2 zonos".</summary>
    public string? PlanLabel { get; set; }

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
        PlanType.Zoned => "Laiko zonų (fiksuota)",
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
