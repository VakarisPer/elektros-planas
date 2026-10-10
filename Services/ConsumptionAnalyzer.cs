using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using vamos.Models;

namespace vamos.Services;

/// <summary>
/// Rule-based "model" that reads an uploaded electricity consumption file and
/// infers a few survey answers (monthly consumption, peak usage period).
/// It is intentionally tolerant: it detects the delimiter, the date column and
/// the kWh column instead of requiring one exact format.
/// </summary>
public sealed class ConsumptionAnalyzer
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy.MM.dd HH:mm", "yyyy.MM.dd",
        "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
        "dd-MM-yyyy HH:mm", "dd-MM-yyyy",
        "dd.MM.yyyy HH:mm", "dd.MM.yyyy",
        "MM/dd/yyyy HH:mm", "MM/dd/yyyy",
        "yyyy-MM"
    };

    private static readonly string[] ValueKeywords =
    {
        "kwh", "kw/h", "mwh", "suvartoj", "kiekis", "energij", "consumption", "value", "reading"
    };

    public ConsumptionAnalysis Analyze(Stream stream, string fileName)
    {
        var lines = ReadLines(stream);
        if (lines.Count == 0)
        {
            return Fail(fileName, "Failas tuščias.");
        }

        var delimiter = DetectDelimiter(lines.Take(30).ToList());
        var rows = lines
            .Select(l => l.Split(delimiter).Select(f => f.Trim().Trim('"').Trim()).ToArray())
            .ToList();

        var hasHeader = LooksLikeHeader(rows[0]);
        var header = hasHeader ? rows[0] : null;
        var dataRows = hasHeader ? rows.Skip(1).ToList() : rows;

        if (dataRows.Count == 0)
        {
            return Fail(fileName, "Nerasta duomenų eilučių.");
        }

        // ESO exports carry the object number – keep only that, never names or personal codes.
        string? objectNumber = null;
        var objectColumn = header is null ? -1 : Array.FindIndex(header, h => h.StartsWith("Objekto nr", StringComparison.OrdinalIgnoreCase));
        if (objectColumn >= 0)
        {
            objectNumber = dataRows
                .Where(r => r.Length > objectColumn)
                .Select(r => r[objectColumn])
                .FirstOrDefault(v => EsoConnectionService.IsValidObjectNumber(v));
        }

        // ESO exports mix energy types: P+ = bought from the grid, P- = given back to the grid
        // (solar), Q+/Q- = reactive energy. Only P+ is consumption; P- is kept as export.
        List<string[]> exportRows = new();
        var typeColumn = header is null ? -1 : Array.FindIndex(header, h => h.Contains("energijos tipas", StringComparison.OrdinalIgnoreCase));
        if (typeColumn >= 0 && dataRows.Any(r => r.Length > typeColumn && r[typeColumn] == "P+"))
        {
            exportRows = dataRows.Where(r => r.Length > typeColumn && r[typeColumn] == "P-").ToList();
            dataRows = dataRows.Where(r => r.Length > typeColumn && r[typeColumn] == "P+").ToList();
        }

        var columnCount = dataRows.Max(r => r.Length);
        var dateColumn = FindDateColumn(dataRows, columnCount);
        var valueColumn = FindValueColumn(dataRows, columnCount, dateColumn, header);
        var timeColumn = FindTimeColumn(dataRows, columnCount, dateColumn, valueColumn);

        // Consumers without a plant still get P- rows, all zero: that is not a prosumer.
        if (valueColumn >= 0 && SumValues(exportRows, valueColumn) <= 1)
        {
            exportRows = new List<string[]>();
        }

        if (valueColumn < 0)
        {
            return Fail(fileName, "Nepavyko aptikti suvartojimo (kWh) stulpelio.");
        }

        var readings = new List<(DateTime? When, double Value)>();
        foreach (var row in dataRows)
        {
            if (row.Length <= valueColumn)
            {
                continue;
            }

            var value = ParseNumber(row[valueColumn]);
            if (value is null or <= 0)
            {
                continue;
            }

            DateTime? when = null;
            if (dateColumn >= 0 && row.Length > dateColumn)
            {
                when = ParseDate(row[dateColumn]);

                // Some exports keep the time of day in a separate column.
                if (when is { } date && date.TimeOfDay == TimeSpan.Zero
                    && timeColumn >= 0 && row.Length > timeColumn
                    && ParseTime(row[timeColumn]) is { } time)
                {
                    when = date.Date.Add(time);
                }
            }

            readings.Add((when, value.Value));
        }

        if (readings.Count == 0)
        {
            return Fail(fileName, "Nepavyko nuskaityti jokių skaitinių reikšmių.");
        }

        var total = readings.Sum(r => r.Value);
        int months;
        double monthlyAverage;

        var isHourly = readings.All(r => r.When is not null)
                       && readings.Select(r => r.When!.Value.Hour).Distinct().Count() > 1;

        if (isHourly)
        {
            // Partial first/last months must not count as full months.
            var first = readings.Min(r => r.When!.Value);
            var last = readings.Max(r => r.When!.Value);
            var spanMonths = Math.Max(1.0 / 30, ((last - first).TotalHours + 1) / (24 * 30.4375));
            months = Math.Max(1, (int)Math.Round(spanMonths));
            monthlyAverage = total / spanMonths;
        }
        else if (readings.All(r => r.When is not null))
        {
            months = Math.Max(1, readings.GroupBy(r => new { r.When!.Value.Year, r.When!.Value.Month }).Count());
            monthlyAverage = total / months;
        }
        else if (readings.Count == 12)
        {
            months = 12;
            monthlyAverage = total / 12;
        }
        else
        {
            return Fail(fileName,
                "Nepavyko nustatyti datų. Įkelkite CSV su datos stulpeliu arba 12 mėnesinių reikšmių.");
        }

        double? nightShare = null;
        string? peakLabel = null;

        var withTime = isHourly ? readings : new List<(DateTime? When, double Value)>();

        if (withTime.Count > 0)
        {
            var timeTotal = withTime.Sum(r => r.Value);
            var nightKwh = withTime.Where(r => IsNight(r.When!.Value)).Sum(r => r.Value);
            nightShare = timeTotal > 0 ? nightKwh / timeTotal : null;

            var buckets = new Dictionary<string, double>
            {
                ["Ryte (6–9 val.)"] = 0,
                ["Dieną (9–17 val.)"] = 0,
                ["Vakare (17–22 val.)"] = 0,
                ["Naktį (22–6 val.)"] = 0
            };

            foreach (var reading in withTime)
            {
                buckets[BucketOf(reading.When!.Value.Hour)] += reading.Value;
            }

            peakLabel = buckets.OrderByDescending(kv => kv.Value).First().Key;
        }

        // Month × day type × hour profile (small enough for localStorage, enough for the tariff zones).
        double[]? importMatrix = null, exportMatrix = null;
        if (isHourly)
        {
            var first = readings.Min(r => r.When!.Value);
            var last = readings.Max(r => r.When!.Value);
            importMatrix = BuildMatrix(readings.Select(r => (r.When!.Value, r.Value)), first, last, isExport: false);

            if (exportRows.Count > 0 && dateColumn >= 0)
            {
                var exports = exportRows
                    .Where(r => r.Length > Math.Max(dateColumn, valueColumn))
                    .Select(r => (When: ParseDate(r[dateColumn]), Value: ParseNumber(r[valueColumn])))
                    .Where(r => r.When is not null && r.Value is > 0)
                    .Select(r => (r.When!.Value, r.Value!.Value));
                exportMatrix = BuildMatrix(exports, first, last, isExport: true);
            }
        }

        return new ConsumptionAnalysis
        {
            Success = true,
            FileName = fileName,
            TotalKwh = total,
            Months = months,
            Readings = readings.Count,
            MonthlyAverageKwh = monthlyAverage,
            NightShare = nightShare,
            PeakPeriodLabel = peakLabel,
            // Same period as P+, so the same months divisor applies.
            ExportedMonthlyKwh = exportRows.Count == 0 ? null : SumValues(exportRows, valueColumn) * monthlyAverage / total,
            ObjectNumber = objectNumber,
            ImportMatrix = importMatrix,
            ExportMatrix = exportMatrix
        };
    }

    /// <summary>
    /// Average kWh per hour for every <see cref="TariffCalendar.Cell576(DateTime)"/> cell
    /// (month × {workday, weekend/holiday} × hour) over the covered period.
    /// A missing day type is copied from the other one of the same month; months missing from the
    /// file are copied from the nearest covered month with a seasonal correction.
    /// </summary>
    private static double[] BuildMatrix(IEnumerable<(DateTime When, double Value)> readings, DateTime first, DateTime last, bool isExport)
    {
        const int cells = 576;
        var sum = new double[cells];
        var hours = new int[cells];
        for (var t = first; t <= last; t = t.AddHours(1))
        {
            hours[TariffCalendar.Cell576(t)]++;
        }

        foreach (var (when, value) in readings)
        {
            sum[TariffCalendar.Cell576(when)] += value;
        }

        var matrix = new double[cells];
        var has = new bool[cells];
        for (var c = 0; c < cells; c++)
        {
            if (hours[c] > 0)
            {
                matrix[c] = sum[c] / hours[c];
                has[c] = true;
            }
        }

        bool DayTypeCovered(int month, int free) => Enumerable.Range(0, 24).Any(h => has[(month * 2 + free) * 24 + h]);

        var covered = new bool[12];
        for (var m = 0; m < 12; m++)
        {
            var work = DayTypeCovered(m, 0);
            var free = DayTypeCovered(m, 1);
            covered[m] = work || free;
            if (work != free)
            {
                var from = work ? 0 : 1;
                for (var h = 0; h < 24; h++)
                {
                    matrix[(m * 2 + 1 - from) * 24 + h] = matrix[(m * 2 + from) * 24 + h];
                }
            }
        }

        if (!covered.Any(x => x))
        {
            return matrix;
        }

        // Seasonal weight: consumption peaks in January, export follows the sun.
        var gen = SolarSimulator.GenerationMatrixPerKwp(1);
        double Season(int month, int hour) => isExport
            ? gen[month * 24 + hour]
            : 1 + 0.15 * Math.Cos(2 * Math.PI * month * 30.4 / 365); // same winter boost as build_load

        for (var m = 0; m < 12; m++)
        {
            if (covered[m])
            {
                continue;
            }

            var source = Enumerable.Range(1, 6)
                .SelectMany(d => new[] { (m + d) % 12, (m - d + 12) % 12 })
                .First(x => covered[x]);

            for (var f = 0; f < 2; f++)
            {
                for (var h = 0; h < 24; h++)
                {
                    var from = Season(source, h);
                    matrix[(m * 2 + f) * 24 + h] = from > 1e-9 ? matrix[(source * 2 + f) * 24 + h] * Season(m, h) / from : 0;
                }
            }
        }

        for (var c = 0; c < cells; c++)
        {
            matrix[c] = Math.Round(matrix[c], 3);
        }

        return matrix;
    }

    private static List<string> ReadLines(Stream stream)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) is not null && lines.Count < 300000)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line.Trim());
            }
        }

        return lines;
    }

    private static char DetectDelimiter(List<string> sample)
    {
        char[] candidates = { ';', ',', '\t', '|' };
        var best = ',';
        double bestAverage = 0;

        foreach (var candidate in candidates)
        {
            var average = sample.Average(l => l.Count(ch => ch == candidate));
            if (average > bestAverage)
            {
                bestAverage = average;
                best = candidate;
            }
        }

        return best;
    }

    private static bool LooksLikeHeader(string[] row) =>
        row.Any(f => !string.IsNullOrWhiteSpace(f)
                     && f.Any(char.IsLetter)
                     && ParseDate(f) is null
                     && ParseNumber(f) is null);

    private static int FindDateColumn(List<string[]> rows, int columnCount)
    {
        var best = -1;
        var bestRatio = 0.6;

        for (var c = 0; c < columnCount; c++)
        {
            var total = 0;
            var ok = 0;
            foreach (var row in rows)
            {
                if (row.Length <= c || string.IsNullOrWhiteSpace(row[c]))
                {
                    continue;
                }

                total++;
                if (LooksLikeDate(row[c]))
                {
                    ok++;
                }
            }

            if (total == 0)
            {
                continue;
            }

            var ratio = (double)ok / total;
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                best = c;
            }
        }

        return best;
    }

    private static int FindTimeColumn(List<string[]> rows, int columnCount, int dateColumn, int valueColumn)
    {
        var best = -1;
        var bestRatio = 0.6;

        for (var c = 0; c < columnCount; c++)
        {
            if (c == dateColumn || c == valueColumn)
            {
                continue;
            }

            var total = 0;
            var ok = 0;
            foreach (var row in rows)
            {
                if (row.Length <= c || string.IsNullOrWhiteSpace(row[c]))
                {
                    continue;
                }

                total++;
                if (ParseTime(row[c]) is not null)
                {
                    ok++;
                }
            }

            if (total == 0)
            {
                continue;
            }

            var ratio = (double)ok / total;
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                best = c;
            }
        }

        return best;
    }

    private static int FindValueColumn(List<string[]> rows, int columnCount, int dateColumn, string[]? header)
    {
        if (header is not null)
        {
            // Keywords are ordered by priority ("kwh" beats "suvartoj", which may be a status column).
            foreach (var keyword in ValueKeywords)
            {
                for (var c = 0; c < header.Length; c++)
                {
                    if (c != dateColumn && header[c].Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        && rows.Any(r => r.Length > c && ParseNumber(r[c]) is not null))
                    {
                        return c;
                    }
                }
            }
        }

        var best = -1;
        var bestMean = 0.0;

        for (var c = 0; c < columnCount; c++)
        {
            if (c == dateColumn)
            {
                continue;
            }

            var numbers = new List<double>();
            foreach (var row in rows)
            {
                if (row.Length <= c || string.IsNullOrWhiteSpace(row[c]))
                {
                    continue;
                }

                if (ParseNumber(row[c]) is { } v)
                {
                    numbers.Add(v);
                }
            }

            if (numbers.Count == 0)
            {
                continue;
            }

            // Skip columns that only contain year-like integers.
            if (numbers.All(n => n >= 1990 && n <= 2100 && n == Math.Floor(n)))
            {
                continue;
            }

            var mean = numbers.Average();
            if (mean > bestMean)
            {
                bestMean = mean;
                best = c;
            }
        }

        return best;
    }

    private static double? ParseNumber(string raw)
    {
        var s = Regex.Replace(raw.Trim(), @"[^0-9,.\-+]", string.Empty);
        if (s.Length == 0)
        {
            return null;
        }

        if (s.Contains(',') && !s.Contains('.'))
        {
            s = s.Replace(',', '.');
        }
        else if (s.Contains(',') && s.Contains('.'))
        {
            s = s.Replace(",", string.Empty);
        }

        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static readonly string[] IsoFormats =
    {
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm"
    };

    private static bool LooksLikeDate(string raw) =>
        TryParseIso(raw.Trim()) is not null ||
        DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>ESO timestamps carry an offset (+02:00/+03:00); keep the local wall-clock time.</summary>
    private static DateTime? TryParseIso(string s) =>
        DateTimeOffset.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.DateTime
            : null;

    private static double SumValues(List<string[]> rows, int column) =>
        rows.Where(r => r.Length > column).Sum(r => ParseNumber(r[column]) is { } v and > 0 ? v : 0);

    /// <summary>
    /// Time of day like "7:00" or "07:00:00". Plain numbers are rejected: <see cref="TimeSpan.TryParse(string, IFormatProvider, out TimeSpan)"/>
    /// would read "22" (permitted power) or "10000001" (customer code) as days.
    /// </summary>
    private static TimeSpan? ParseTime(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0 || !s.Contains(':'))
        {
            return null;
        }

        string[] formats = { @"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss" };
        return TimeSpan.TryParseExact(s, formats, CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1)
            ? time
            : null;
    }

    private static DateTime? ParseDate(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0)
        {
            return null;
        }

        if (TryParseIso(s) is { } iso)
        {
            return iso;
        }

        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            return exact;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant))
        {
            return invariant;
        }

        if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("lt-LT"), DateTimeStyles.None, out var lithuanian))
        {
            return lithuanian;
        }

        return null;
    }

    private static bool IsNight(DateTime when) => when.Hour is < 6 or >= 22;

    private static string BucketOf(int hour) => hour switch
    {
        < 6 => "Naktį (22–6 val.)",
        < 9 => "Ryte (6–9 val.)",
        < 17 => "Dieną (9–17 val.)",
        < 22 => "Vakare (17–22 val.)",
        _ => "Naktį (22–6 val.)"
    };

    private static ConsumptionAnalysis Fail(string fileName, string error) =>
        new() { Success = false, FileName = fileName, Error = error };
}
