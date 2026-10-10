// Parity check: C# SolarSimulator vs tools/solar_calc.py (flat price, no arbitrage).
// Run from the repo's project folder:  dotnet run tools/parity/check.cs
#:project ../../vamos.csproj
using System.Diagnostics;
using System.Text.Json;
using vamos.Services;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "tools/parity", "..", ".."));
var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "parity", "cases.json"))).RootElement;
var p = new SolarOptions(); // defaults mirror solar_calc.py DEFAULTS
const double price = 0.20, exportPrice = 0.06;
var failures = 0;

foreach (var c in cases.EnumerateArray())
{
    var type = c.GetProperty("type").GetString()!;
    var kwh = c.GetProperty("kwh").GetDouble();
    var peak = c.GetProperty("peak").GetString()!;

    var psi = new ProcessStartInfo("python", ["-I", "-X", "utf8", Path.Combine(root, "tools", "solar_calc.py"),
        "--type", type, "--kwh", kwh.ToString(System.Globalization.CultureInfo.InvariantCulture), "--peak-hours", peak, "--json"])
    { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
    using var proc = Process.Start(psi)!;
    var py = JsonDocument.Parse(proc.StandardOutput.ReadToEnd()).RootElement;
    proc.WaitForExit();

    var load = LoadProfileBuilder.Synthetic(kwh, LoadProfileBuilder.ParseHours(peak));
    var house = type == "namas";
    var scan = SolarSimulator.Analyze(load,
        house ? p.HouseKwpOptions : p.FlatKwpOptions,
        house ? p.HouseBatteryOptions : p.FlatBatteryOptions,
        r => r.Import * price - r.Export * exportPrice,
        p, new SimOptions(p.Yield, p.BatteryEff, p.Dod, p.CRate), p.PvCostPerKwp);

    void Compare(string label, JsonElement expected, SolarOption? actual)
    {
        if (actual is null) { Console.WriteLine($"  {label}: missing"); failures++; return; }
        var ok = expected.GetProperty("pv_kwp").GetDouble() == actual.PvKwp
                 && expected.GetProperty("batt_kwh").GetDouble() == actual.BattKwh
                 && Close(expected.GetProperty("npv_eur").GetDouble(), actual.Npv)
                 && Close(expected.GetProperty("year1_savings_eur").GetDouble(), actual.Year1Savings)
                 && Close(expected.GetProperty("generation_kwh").GetDouble(), actual.Generation);
        var pyPb = expected.GetProperty("payback_years");
        ok &= pyPb.ValueKind == JsonValueKind.Null ? actual.PaybackYears is null
                                                  : actual.PaybackYears is { } pb && Math.Abs(pb - pyPb.GetDouble()) <= 0.15;
        if (!ok) failures++;
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}: py {expected.GetProperty("pv_kwp")} kWp+{expected.GetProperty("batt_kwh")} NPV {expected.GetProperty("npv_eur")} pb {pyPb} | cs {actual.PvKwp} kWp+{actual.BattKwh} NPV {actual.Npv} pb {actual.PaybackYears}");
    }

    Console.WriteLine($"{c.GetProperty("name").GetString()}: verdict py solar={py.GetProperty("solar_worth_it")} batt={py.GetProperty("battery_worth_it")} | cs solar={scan.SolarWorthIt} batt={scan.BatteryWorthIt}");
    if (py.GetProperty("solar_worth_it").GetBoolean() != scan.SolarWorthIt || py.GetProperty("battery_worth_it").GetBoolean() != scan.BatteryWorthIt) failures++;
    Compare("best_overall", py.GetProperty("best_overall"), scan.BestOverall);
    Compare("best_pv_only", py.GetProperty("best_pv_only"), scan.BestPvOnly);
    Compare("best_with_battery", py.GetProperty("best_with_battery"), scan.BestWithBattery);
}

Console.WriteLine(failures == 0 ? "PARITY OK" : $"PARITY FAILED ({failures})");
return failures == 0 ? 0 : 1;

static bool Close(double a, double b) => Math.Abs(a - b) <= Math.Max(2, Math.Abs(a) * 0.01);
