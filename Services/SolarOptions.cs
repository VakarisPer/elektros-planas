namespace vamos.Services;

/// <summary>How a prosumer settles surplus energy with ESO / the supplier.</summary>
public enum ProsumerScheme
{
    /// <summary>Dvipusė apskaita: export goes to a kWh bank and is taken back for an ESO fee (modes 1–4).</summary>
    NetMetering,

    /// <summary>Grynasis atsiskaitymas: export earns (np − fee) into a money wallet that pays the bill.</summary>
    NetBilling
}

/// <summary>
/// Solar / battery investment assumptions (section "Solar"), DEMO values from tools/solar_calc.py –
/// verify against real installer prices. Grid prices and prosumer rules live in Data/Tariffs.
/// </summary>
public sealed class SolarOptions
{
    public double PvCostPerKwp { get; set; } = 1100;

    public double BatteryCostPerKwh { get; set; } = 550;

    /// <summary>Hybrid inverter / installation extra when a battery is added to an existing plant, EUR.</summary>
    public double BatteryRetrofitExtra { get; set; } = 800;

    /// <summary>kWh per kWp per year (Vilnius, south roof).</summary>
    public double Yield { get; set; } = 950;

    /// <summary>APVA compensation for a new solar plant, EUR per kW (DEMO).</summary>
    public double ApvaPvPerKw { get; set; } = 255;

    /// <summary>Largest plant size APVA compensates, kW.</summary>
    public double ApvaPvMaxKw { get; set; } = 10;

    /// <summary>APVA compensation for a home battery, EUR per kWh (DEMO).</summary>
    public double ApvaBatteryPerKwh { get; set; } = 150;

    /// <summary>Largest battery compensation, EUR (DEMO).</summary>
    public double ApvaBatteryMaxEur { get; set; } = 1500;

    public double Discount { get; set; } = 0.04;

    public double PriceGrowth { get; set; } = 0.02;

    public int Horizon { get; set; } = 25;

    public double PvDegradation { get; set; } = 0.005;

    public double BatteryEff { get; set; } = 0.90;

    public double CRate { get; set; } = 0.5;

    public int BatteryLife { get; set; } = 15;

    public double BatteryFade { get; set; } = 0.02;

    public int InverterYear { get; set; } = 12;

    public double InverterCost { get; set; } = 150;

    /// <summary>Capacity assumed for a battery the user already has, kWh.</summary>
    public double AssumedExistingBatteryKwh { get; set; } = 10;

    public double[] HouseKwpOptions { get; set; } = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

    public double[] HouseBatteryOptions { get; set; } = { 0, 5, 10, 15 };

}
