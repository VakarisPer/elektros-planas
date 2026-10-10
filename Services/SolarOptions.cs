namespace vamos.Services;

/// <summary>How a prosumer settles surplus energy with ESO / the supplier.</summary>
public enum ProsumerScheme
{
    /// <summary>Surplus is "stored" in the grid and taken back later for a fee (kaupimas tinkle).</summary>
    NetMetering,

    /// <summary>Surplus is sold at <see cref="SolarOptions.ExportPrice"/>.</summary>
    SellSurplus
}

/// <summary>
/// Solar / battery economics (section "Solar"). DEMO assumptions ported from tools/solar_calc.py –
/// verify against real installer prices, ESO tariffs and the current prosumer rules.
/// </summary>
public sealed class SolarOptions
{
    /// <summary>ESO distribution + other per-kWh charges with VAT, EUR/kWh (avoided by self-consumption).</summary>
    public double DistributionPrice { get; set; } = 0.085;

    /// <summary>Fee per kWh taken back from the grid under net metering, EUR/kWh.</summary>
    public double NetMeteringFee { get; set; } = 0.06;

    /// <summary>Price paid for sold surplus, EUR/kWh.</summary>
    public double ExportPrice { get; set; } = 0.06;

    public ProsumerScheme DefaultScheme { get; set; } = ProsumerScheme.NetMetering;

    public double PvCostPerKwp { get; set; } = 1100;

    public double BatteryCostPerKwh { get; set; } = 550;

    /// <summary>Hybrid inverter / installation extra when a battery is added to an existing plant, EUR.</summary>
    public double BatteryRetrofitExtra { get; set; } = 800;

    /// <summary>Price of a remote solar park share, EUR/kW.</summary>
    public double RemoteParkCostPerKw { get; set; } = 750;

    /// <summary>Yield multiplier for vertical / shaded balcony panels.</summary>
    public double BalconyYieldFactor { get; set; } = 0.65;

    /// <summary>kWh per kWp per year (Vilnius, south roof).</summary>
    public double Yield { get; set; } = 950;

    public double SubsidyPct { get; set; }

    public double Discount { get; set; } = 0.04;

    public double PriceGrowth { get; set; } = 0.02;

    public int Horizon { get; set; } = 25;

    public double PvDegradation { get; set; } = 0.005;

    public double BatteryEff { get; set; } = 0.90;

    public double Dod { get; set; } = 0.90;

    public double CRate { get; set; } = 0.5;

    public int BatteryLife { get; set; } = 15;

    public double BatteryFade { get; set; } = 0.02;

    public int InverterYear { get; set; } = 12;

    public double InverterCost { get; set; } = 150;

    /// <summary>Capacity assumed for a battery the user already has, kWh.</summary>
    public double AssumedExistingBatteryKwh { get; set; } = 10;

    public double[] HouseKwpOptions { get; set; } = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

    public double[] HouseBatteryOptions { get; set; } = { 0, 5, 10, 15 };

    public double[] FlatKwpOptions { get; set; } = { 0.4, 0.8 };

    public double[] FlatBatteryOptions { get; set; } = { 0, 2 };

    public double[] RemoteParkKwOptions { get; set; } = { 1, 2, 3, 5 };
}
