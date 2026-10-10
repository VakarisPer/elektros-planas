// Checks the bill engine against the worked examples in the project documentation
// ("Testas: …" boxes of ESO / tiekėjo / saulės / kaupiklio dedamoji and Galutinė kaina).
// Run from the project folder:  dotnet run tools/formula-tests/check.cs
#:project ../../vamos.csproj
using vamos.Services;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "tools/formula-tests", "..", ".."));
var catalog = new TariffCatalog(root, new DateTime(2026, 10, 1));
var engine = new BillEngine(catalog);
var failures = 0;

void Check(string label, double actual, double expected, double tolerance = 0.005)
{
    var ok = Math.Abs(actual - expected) <= tolerance;
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}: {actual:0.00} (laukta {expected:0.00})");
}

void CheckTrue(string label, bool ok)
{
    if (!ok) failures++;
    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}");
}

int Hour(int month, int day, int hour) => Array.FindIndex(TariffCalendar.Dates, d => d.Month == month && d.Day == day && d.Hour == hour);

double[] Series(params (int Index, double Kwh)[] values)
{
    var s = new double[TariffCalendar.Hours];
    foreach (var (i, kwh) in values) s[i] += kwh;
    return s;
}

double[] Constant(double value) => Enumerable.Repeat(value, TariffCalendar.Hours).ToArray();

var exchange = new SupplierPlan { Supplier = "Test", Name = "Birža (marža 0,015)", Kind = SupplierKind.Exchange, Margin = 0.015 };
EsoPlan Eso(string id) => catalog.EsoPlan(id);

// 2026-01-05 is a Monday (workday): 10:00 is "Diena", 02:00 is "Naktis" in the 2-zone plan.
var day = Hour(1, 5, 10);
var night = Hour(1, 5, 2);
var month200 = Series((day, 120), (night, 80));

Console.WriteLine("ESO dalis per mėnesį (200 kWh: 120 dieną, 80 naktį, LG = 5 kW):");
foreach (var (id, zones, expected) in new[]
         {
             ("Standartinis", 1, 22.26), ("Standartinis", 2, 21.25), ("Namai", 1, 22.12),
             ("Namai", 2, 21.15), ("Efektyvus", 1, 22.67), ("Efektyvus", 2, 21.75)
         })
{
    var bill = engine.Price(FlowAggregates.From(month200, null, Constant(0.09)), new PlanCombo(Eso(id), zones, exchange), lgKw: 5);
    Check($"{id}, {zones} z.", bill.EsoLines.Sum(l => l.Eur) + bill.EsoFixed / 12, expected);
}

Console.WriteLine("Tiekėjo dalis (200 kWh, birža, marža 0,015):");
foreach (var (np, expected) in new[] { (0.06, 18.15), (0.12, 32.67) })
{
    var bill = engine.Price(FlowAggregates.From(month200, null, Constant(np)), new PlanCombo(Eso("Standartinis"), 1, exchange), lgKw: 5);
    Check($"np = {np}", bill.Energy, expected);
}

Console.WriteLine("Visa mėnesio sąskaita (ESO Namai, 2 zonos, birža, np = 0,09):");
{
    var bill = engine.Price(FlowAggregates.From(month200, null, Constant(0.09)), new PlanCombo(Eso("Namai"), 2, exchange), lgKw: 5);
    Check("ESO diena", bill.EsoLines[0].Eur, 13.21);
    Check("ESO naktis", bill.EsoLines[1].Eur, 4.94);
    Check("ESO pastovus", bill.EsoFixed / 12, 3.00);
    Check("Energija", bill.Energy, 25.41);
    Check("VIAP + DED", bill.ViapDed, -0.05);
    Check("Iš viso (sausis)", bill.PerMonth[0], 46.51);
}

Console.WriteLine("Saulės elektrinė 10 kW per metus (eksp 6 650, kwh 4 150):");
{
    // Export in July, import in January: the kWh bank fills in summer and is used in winter.
    var import = Series((Hour(1, 5, 19), 4150));
    var export = Series((Hour(7, 6, 13), 6650));
    // Full price 0,238 €/kWh = ESO Standartinis 1 zona + fixed energy + VIAP/DED.
    var full = 0.238 - 0.11132 - (catalog.Eso.Viap + catalog.Eso.Ded);
    var fixedPlan = new SupplierPlan
    {
        Supplier = "Test", Name = "Fiksuota", Kind = SupplierKind.Fixed, Zones = 1,
        Prices = new Dictionary<TariffZone, double> { [TariffZone.All] = full }
    };
    var combo = new PlanCombo(Eso("Standartinis"), 1, fixedPlan);
    var agg = FlowAggregates.From(import, export, Constant(0.05));

    var m1 = engine.Price(agg, combo, 0, settlement: new ProsumerSettlement(ProsumerScheme.NetMetering, 1, 10));
    Check("1 būdas (ESO mokestis)", m1.ProsumerFee, 301.29, 0.5);
    Check("1 būdas – banke lieka kWh", m1.BankLeftoverKwh, 2500, 0.5);
    Check("2 būdas", engine.Price(agg, combo, 0, settlement: new ProsumerSettlement(ProsumerScheme.NetMetering, 2, 10)).ProsumerFee, 604.03, 0.5);
    var m3 = engine.Price(agg, combo, 0, settlement: new ProsumerSettlement(ProsumerScheme.NetMetering, 3, 10));
    Check("3 būdas (ESO mokestis + trūkumas, be VIAP)", m3.Total - m3.ViapDed, 0, 0.5);
    Check("4 būdas", engine.Price(agg, combo, 0, settlement: new ProsumerSettlement(ProsumerScheme.NetMetering, 4, 10)).ProsumerFee, 461.98, 0.5);
    Check("Grynasis atsiskaitymas", engine.Price(agg, combo, 0, settlement: new ProsumerSettlement(ProsumerScheme.NetBilling, 0, 10)).Total, 819.46, 0.5);

    Console.WriteLine("Kaupiklis su elektrine (1 500 kWh pertekliaus → atgaunama 1 350 kWh):");
    var noBattery = FlowAggregates.From(Series((Hour(1, 5, 19), 1350)), Series((Hour(7, 6, 13), 1500)), Constant(0.05));
    var withBattery = FlowAggregates.From(new double[TariffCalendar.Hours], new double[TariffCalendar.Hours], Constant(0.05));
    var twoWay = new ProsumerSettlement(ProsumerScheme.NetMetering, 1, 10);
    var net = new ProsumerSettlement(ProsumerScheme.NetBilling, 0, 10);
    Check("Dvipusė apskaita", engine.Price(noBattery, combo, 0, settlement: twoWay).Total - engine.Price(withBattery, combo, 0, settlement: twoWay).Total, 98, 0.5);
    Check("Grynasis atsiskaitymas", engine.Price(noBattery, combo, 0, settlement: net).Total - engine.Price(withBattery, combo, 0, settlement: net).Total, 283, 0.5);
}

Console.WriteLine("Kaupiklis be elektrinės (8 kWh iš vakaro į naktį, ESO Standartinis 4 zonos):");
{
    var prices = Eso("Standartinis").Zones[4];
    var price = new double[24];
    var load = new double[24];
    var workday = Hour(1, 5, 0);
    for (var h = 0; h < 24; h++) price[h] = prices[TariffCalendar.Zone4[workday + h]];
    load[18] = 8;
    var run = BatteryArbitrage.Run(load, price, capacityKwh: 8, powerKw: 8, eta: 0.9);
    Check("Per dieną", run.Benefit, 0.61, 0.01);
    Check("Per metus", run.Benefit * 365, 223, 1);
}

Console.WriteLine("Laiko zonos ir šventės:");
CheckTrue("Darbo d. 07:30 žiemą – 2 zonų diena", TariffCalendar.Zone2[Hour(1, 5, 7)] == TariffZone.Day);
CheckTrue("Darbo d. 07:30 vasarą – 2 zonų naktis", TariffCalendar.Zone2[Hour(7, 7, 7)] == TariffZone.Night);
CheckTrue("Darbo d. 23:00 vasarą – 2 zonų diena", TariffCalendar.Zone2[Hour(7, 7, 23)] == TariffZone.Day);
CheckTrue("Šeštadienis 12:00 – 2 zonų naktis", TariffCalendar.Zone2[Hour(1, 10, 12)] == TariffZone.Night);
CheckTrue("12-24 – šventė", TariffCalendar.FreeDay[Hour(12, 24, 12)]);
CheckTrue("07-06 – šventė", TariffCalendar.FreeDay[Hour(7, 6, 12)]);
CheckTrue("Velykų pirmadienis 2026-04-06 – šventė", TariffCalendar.FreeDay[Hour(4, 6, 12)]);
CheckTrue("4 zonos: darbo d. 18:00 – vakaras", TariffCalendar.Zone4[Hour(1, 5, 18)] == TariffZone.Evening);
CheckTrue("4 zonos: darbo d. 05:00 – rytas", TariffCalendar.Zone4[Hour(1, 5, 5)] == TariffZone.Morning);
CheckTrue("4 zonos: sekmadienis 10:00 – diena", TariffCalendar.Zone4[Hour(1, 11, 10)] == TariffZone.Day);

Console.WriteLine("Apribojimai:");
var withMeter = engine.Combos(smartMeter: true, includePublic: false).ToList();
var noMeter = engine.Combos(smartMeter: false, includePublic: false).ToList();
CheckTrue($"Deriniai su išmaniuoju: {withMeter.Count}, be jo: {noMeter.Count}", withMeter.Count > noMeter.Count);
CheckTrue("Be išmaniojo nėra 4 zonų", noMeter.All(c => c.Zones != 4));
CheckTrue("Namai neturi 4 zonų", withMeter.All(c => !(c.Eso.Id == "Namai" && c.Zones == 4)));
CheckTrue("Tiekėjo zonos = ESO zonos (birža – bet kurios)", withMeter.All(c => c.Supplier.Kind == SupplierKind.Exchange || c.Supplier.Zones == c.Zones));
CheckTrue("Visuomeninis tiekimas tik jo vartotojams", withMeter.All(c => c.Supplier.Kind != SupplierKind.Public));
CheckTrue($"Biržos kainos iš failo ({catalog.Np.Length} val., vid. {catalog.NpMean:0.0000})", catalog.NpFromFile && catalog.Np.Length == TariffCalendar.Hours);

Console.WriteLine(failures == 0 ? "FORMULĖS OK" : $"FORMULĖS NESUTAMPA ({failures})");
return failures == 0 ? 0 : 1;
