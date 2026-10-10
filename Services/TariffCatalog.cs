using System.Globalization;
using System.Text.Json;

namespace vamos.Services;

/// <summary>One ESO plan (Standartinis / Efektyvus / Namai) with its zone prices, EUR/kWh with VAT.</summary>
public sealed class EsoPlan
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Fixed fee per month, EUR.</summary>
    public double FixedMonthly { get; init; }

    /// <summary>Fixed fee per kW of permitted power (LG) per month, EUR.</summary>
    public double FixedPerLgKw { get; init; }

    /// <summary>Zone count (1, 2, 4) → zone → price.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<TariffZone, double>> Zones { get; init; } =
        new Dictionary<int, IReadOnlyDictionary<TariffZone, double>>();

    public double MonthlyFee(double lgKw) => FixedMonthly + FixedPerLgKw * lgKw;
}

/// <summary>ESO tariffs valid from one date.</summary>
public sealed class EsoPeriod
{
    public DateTime ValidFrom { get; init; }

    /// <summary>Public service obligation charge (VIAP), EUR/kWh with VAT (may be negative).</summary>
    public double Viap { get; init; }

    /// <summary>Additional component (DED), EUR/kWh with VAT.</summary>
    public double Ded { get; init; }

    public IReadOnlyList<EsoPlan> Plans { get; init; } = Array.Empty<EsoPlan>();
}

/// <summary>Prosumer settlement rules (two-way metering modes, net billing, accumulation period).</summary>
public sealed class ProsumerRules
{
    /// <summary>Mode 1: fee per kWh taken back, EUR.</summary>
    public double Mode1PerKwh { get; init; } = 0.0726;

    /// <summary>Mode 2: fee per kW of permitted generation (LGG) per month, EUR.</summary>
    public double Mode2PerLggKwMonth { get; init; } = 5.0336;

    /// <summary>Mode 3: share of exported kWh credited to the bank (no ESO fee).</summary>
    public double Mode3BankShare { get; init; } = 0.63;

    /// <summary>Net billing: administration fee subtracted from the spot price of exported kWh, EUR/kWh.</summary>
    public double NetBillingFee { get; init; } = 0.0247;

    public DateTime PeriodStart { get; init; } = new(2026, 4, 1);
    public DateTime PeriodEnd { get; init; } = new(2028, 3, 31);

    /// <summary>Cash payouts of the leftover are taxed (GPM) from this amount, EUR.</summary>
    public double PayoutTaxFrom { get; init; } = 2500;
}

public enum SupplierKind
{
    /// <summary>Independent supplier, fixed price (1, 2 or 4 zones).</summary>
    Fixed,

    /// <summary>Independent supplier, exchange price (np + margin) × VAT.</summary>
    Exchange,

    /// <summary>Regulated public supply (visuomeninis tiekimas).</summary>
    Public
}

/// <summary>A supplier offer, fields as in the project documentation (plano duomenys).</summary>
public sealed class SupplierPlan
{
    public string Supplier { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public SupplierKind Kind { get; init; }
    public int DurationMonths { get; init; } = 12;

    /// <summary>Zone count; 0 for exchange plans (combine with any ESO zoning).</summary>
    public int Zones { get; init; }

    /// <summary>Zone prices, EUR/kWh as written in the offer (see <see cref="WithVat"/>).</summary>
    public IReadOnlyDictionary<TariffZone, double> Prices { get; init; } = new Dictionary<TariffZone, double>();

    /// <summary>Exchange margin, EUR/kWh without VAT.</summary>
    public double Margin { get; init; }

    public double MonthlyFee { get; init; }
    public bool WithVat { get; init; } = true;

    /// <summary>The price already contains the ESO part – do not add it.</summary>
    public bool IncludesEso { get; init; }

    public DateTime ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }

    /// <summary>Energy price of a zone with VAT, EUR/kWh (not for exchange plans).</summary>
    public double PriceWithVat(TariffZone zone) =>
        (Prices.TryGetValue(zone, out var p) ? p : Prices.Values.FirstOrDefault()) * (WithVat ? 1 : TariffCatalog.Vat);
}

/// <summary>
/// Loads ESO tariffs, supplier plans and the hourly Nord Pool LT prices from <c>Data/</c>.
/// JSON is parsed with <see cref="JsonDocument"/> so it also works where reflection-based
/// serialization is disabled (file-based tools).
/// </summary>
public sealed class TariffCatalog
{
    public const double Vat = 1.21;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public TariffCatalog(string contentRoot, DateTime? today = null)
    {
        var now = today ?? DateTime.Today;
        var dataDir = Path.Combine(contentRoot, "Data");

        using (var eso = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "Tariffs", "eso-tariffs.json"))))
        {
            var periods = eso.RootElement.GetProperty("Periods").EnumerateArray().Select(ParsePeriod).OrderBy(p => p.ValidFrom).ToList();
            AllEsoPeriods = periods;
            Eso = periods.LastOrDefault(p => p.ValidFrom <= now) ?? periods.First();
            Prosumer = eso.RootElement.TryGetProperty("Prosumer", out var pr) ? ParseProsumer(pr) : new ProsumerRules();
        }

        using (var plans = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "Tariffs", "supplier-plans.json"))))
        {
            AllSupplierPlans = plans.RootElement.GetProperty("Plans").EnumerateArray().Select(ParseSupplierPlan).ToList();
        }

        // A plan must be valid today and for at least one more month.
        SupplierPlans = AllSupplierPlans
            .Where(p => p.ValidFrom <= now.AddMonths(1) && (p.ValidTo is null || p.ValidTo >= now.AddMonths(1)))
            .ToList();

        var pricePath = Path.Combine(dataDir, "Prices", "np-lt.csv");
        (Np, NpFromFile) = File.Exists(pricePath) ? (LoadPrices(pricePath), true) : (SyntheticPrices(), false);
        NpMean = Np.Average();
    }

    public EsoPeriod Eso { get; }
    public IReadOnlyList<EsoPeriod> AllEsoPeriods { get; }
    public ProsumerRules Prosumer { get; }
    public IReadOnlyList<SupplierPlan> SupplierPlans { get; }
    public IReadOnlyList<SupplierPlan> AllSupplierPlans { get; }

    /// <summary>Nord Pool LT day-ahead price of every hour of <see cref="TariffCalendar"/>, EUR/kWh without VAT.</summary>
    public double[] Np { get; }

    public bool NpFromFile { get; }
    public double NpMean { get; }

    public string NpPeriodLabel => NpFromFile ? "2025-10 – 2026-09" : "sintetinė kreivė";

    public EsoPlan EsoPlan(string id) => Eso.Plans.First(p => p.Id == id);

    // ── Parsing ─────────────────────────────────────────────────────────

    private static EsoPeriod ParsePeriod(JsonElement e) => new()
    {
        ValidFrom = Date(e, "ValidFrom") ?? DateTime.MinValue,
        Viap = Num(e, "Viap"),
        Ded = Num(e, "Ded"),
        Plans = e.GetProperty("Plans").EnumerateArray().Select(p => new EsoPlan
        {
            Id = Str(p, "Id") ?? string.Empty,
            Description = Str(p, "Description") ?? string.Empty,
            FixedMonthly = Num(p, "FixedMonthly"),
            FixedPerLgKw = Num(p, "FixedPerLgKw"),
            Zones = p.GetProperty("Zones").EnumerateObject().ToDictionary(
                z => int.Parse(z.Name, Invariant),
                z => (IReadOnlyDictionary<TariffZone, double>)ParseZonePrices(z.Value))
        }).ToList()
    };

    private static ProsumerRules ParseProsumer(JsonElement e) => new()
    {
        Mode1PerKwh = Num(e, "Mode1PerKwh", 0.0726),
        Mode2PerLggKwMonth = Num(e, "Mode2PerLggKwMonth", 5.0336),
        Mode3BankShare = Num(e, "Mode3BankShare", 0.63),
        NetBillingFee = Num(e, "NetBillingFee", 0.0247),
        PeriodStart = Date(e, "PeriodStart") ?? new DateTime(2026, 4, 1),
        PeriodEnd = Date(e, "PeriodEnd") ?? new DateTime(2028, 3, 31),
        PayoutTaxFrom = Num(e, "PayoutTaxFrom", 2500)
    };

    private static SupplierPlan ParseSupplierPlan(JsonElement e)
    {
        var kind = (Str(e, "tipas") ?? "fiksuota").ToLowerInvariant() switch
        {
            "birza" or "birža" => SupplierKind.Exchange,
            "visuomeninis" => SupplierKind.Public,
            _ => SupplierKind.Fixed
        };

        var prices = e.TryGetProperty("kainos", out var k) ? ParseZonePrices(k) : new Dictionary<TariffZone, double>();
        return new SupplierPlan
        {
            Supplier = Str(e, "tiekejas") ?? string.Empty,
            Name = Str(e, "pavadinimas") ?? Str(e, "tiekejas") ?? string.Empty,
            Kind = kind,
            DurationMonths = (int)Num(e, "trukme_men", 12),
            Zones = kind == SupplierKind.Exchange ? 0 : (int)Num(e, "zonos", prices.Count == 4 ? 4 : prices.Count == 2 ? 2 : 1),
            Prices = prices,
            Margin = Num(e, "marza"),
            MonthlyFee = Num(e, "men_mokestis"),
            WithVat = !e.TryGetProperty("su_pvm", out var vat) || vat.ValueKind != JsonValueKind.False,
            IncludesEso = e.TryGetProperty("iskaicia_eso", out var inc) && inc.ValueKind == JsonValueKind.True,
            ValidFrom = Date(e, "galioja_nuo") ?? DateTime.MinValue,
            ValidTo = Date(e, "galioja_iki")
        };
    }

    private static Dictionary<TariffZone, double> ParseZonePrices(JsonElement e)
    {
        var result = new Dictionary<TariffZone, double>();
        foreach (var z in e.EnumerateObject())
        {
            if (TariffCalendar.TryParseZone(z.Name, out var zone))
            {
                result[zone] = z.Value.GetDouble();
            }
        }

        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParseExact(s, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Maps the CSV (date, hour, price) onto the reference calendar; missing hours keep the previous price.</summary>
    private static double[] LoadPrices(string path)
    {
        var byHour = new Dictionary<DateTime, double>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 3 ||
                !DateTime.TryParseExact(parts[0], "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var day) ||
                !int.TryParse(parts[1], Invariant, out var hour) ||
                !double.TryParse(parts[2], NumberStyles.Float, Invariant, out var price))
            {
                continue;
            }

            byHour[day.AddHours(hour)] = price;
        }

        var np = new double[TariffCalendar.Hours];
        var previous = byHour.Count > 0 ? byHour.Values.Average() : 0.1;
        for (var i = 0; i < np.Length; i++)
        {
            var when = TariffCalendar.Dates[i];
            // Fall back to the same hour a year earlier/later when the file covers another year.
            if (byHour.TryGetValue(when, out var p) || byHour.TryGetValue(when.AddYears(-1), out p) || byHour.TryGetValue(when.AddYears(1), out p))
            {
                previous = p;
            }

            np[i] = previous;
        }

        return np;
    }

    /// <summary>Rough LT price shape used only when the price file is missing.</summary>
    private static double[] SyntheticPrices()
    {
        double[] month = { 1.35, 1.3, 1.1, 0.9, 0.8, 0.8, 0.85, 0.85, 0.95, 1.05, 1.2, 1.35 };
        var np = new double[TariffCalendar.Hours];
        for (var i = 0; i < np.Length; i++)
        {
            var hour = TariffCalendar.Dates[i].Hour;
            var shape = hour is >= 23 or < 7 ? 0.7 : hour >= 17 ? 1.35 : 0.95;
            np[i] = 0.09 * month[TariffCalendar.Month[i]] * shape;
        }

        return np;
    }
}
