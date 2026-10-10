namespace vamos.Services;

/// <summary>Time zone of an ESO / supplier tariff (2- or 4-zone plans).</summary>
public enum TariffZone
{
    All,
    Day,
    Night,
    Morning,
    Evening
}

/// <summary>
/// The reference year all hourly calculations run on, plus the ESO time-zone rules.
/// Hour index 0 = 1 January 00:00 local time; 8760 hours (non-leap). Months Oct–Dec are taken from
/// 2025 and Jan–Sep from 2026, so every hour has a real date (weekday, holiday) matching the
/// Nord Pool price file <c>Data/Prices/np-lt.csv</c> (2025-10-01 … 2026-09-30).
/// </summary>
public static class TariffCalendar
{
    public const int Hours = 8760;

    /// <summary>Public holidays (month, day) billed like weekends, plus Easter Sunday and Monday.</summary>
    private static readonly (int Month, int Day)[] FixedHolidays =
    {
        (1, 1), (2, 16), (3, 11), (5, 1), (6, 24), (7, 6), (8, 15), (11, 1), (12, 24), (12, 25), (12, 26)
    };

    /// <summary>Real date of every hour (local clock).</summary>
    public static readonly DateTime[] Dates = BuildDates();

    /// <summary>Month 0–11 of every hour.</summary>
    public static readonly int[] Month = Dates.Select(d => d.Month - 1).ToArray();

    /// <summary>True on Saturdays, Sundays and public holidays.</summary>
    public static readonly bool[] FreeDay = Dates.Select(d => IsFreeDay(d)).ToArray();

    public static readonly bool[] SummerTime = Dates.Select(IsSummerTime).ToArray();

    /// <summary>Zone of every hour for 1-, 2- and 4-zone plans.</summary>
    public static readonly TariffZone[] Zone2 = Enumerable.Range(0, Hours).Select(i => ZoneOf(2, i)).ToArray();
    public static readonly TariffZone[] Zone4 = Enumerable.Range(0, Hours).Select(i => ZoneOf(4, i)).ToArray();

    private static DateTime[] BuildDates()
    {
        var dates = new DateTime[Hours];
        var i = 0;
        for (var m = 1; m <= 12; m++)
        {
            var year = m >= 10 ? 2025 : 2026;
            for (var d = 1; d <= DateTime.DaysInMonth(year, m); d++)
            {
                for (var h = 0; h < 24; h++)
                {
                    dates[i++] = new DateTime(year, m, d, h, 0, 0);
                }
            }
        }

        return dates;
    }

    public static TariffZone[] ZonesFor(int zones) => zones switch
    {
        2 => Zone2,
        4 => Zone4,
        _ => AllZone
    };

    private static readonly TariffZone[] AllZone = Enumerable.Repeat(TariffZone.All, Hours).ToArray();

    /// <summary>Zones used by a plan with the given number of zones, in display order.</summary>
    public static TariffZone[] ZoneList(int zones) => zones switch
    {
        2 => new[] { TariffZone.Day, TariffZone.Night },
        4 => new[] { TariffZone.Night, TariffZone.Morning, TariffZone.Day, TariffZone.Evening },
        _ => new[] { TariffZone.All }
    };

    public static string Label(TariffZone zone) => zone switch
    {
        TariffZone.Day => "Diena",
        TariffZone.Night => "Naktis",
        TariffZone.Morning => "Rytas",
        TariffZone.Evening => "Vakaras",
        _ => "Visa para"
    };

    public static bool TryParseZone(string label, out TariffZone zone)
    {
        foreach (var z in Enum.GetValues<TariffZone>())
        {
            if (string.Equals(Label(z), label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(z.ToString(), label, StringComparison.OrdinalIgnoreCase))
            {
                zone = z;
                return true;
            }
        }

        zone = TariffZone.All;
        return false;
    }

    public static TariffZone ZoneOf(int zones, int hourIndex)
    {
        var hour = Dates[hourIndex].Hour;
        var free = FreeDay[hourIndex];
        switch (zones)
        {
            case 2:
                // Day: workdays 7–23 in winter time, 8–24 in summer time; everything else is night.
                if (free)
                {
                    return TariffZone.Night;
                }

                return SummerTime[hourIndex]
                    ? hour >= 8 ? TariffZone.Day : TariffZone.Night
                    : hour is >= 7 and < 23 ? TariffZone.Day : TariffZone.Night;
            case 4:
                if (free)
                {
                    return hour is >= 7 and < 22 ? TariffZone.Day : TariffZone.Night;
                }

                return hour switch
                {
                    >= 22 or < 5 => TariffZone.Night,
                    < 7 => TariffZone.Morning,
                    < 17 => TariffZone.Day,
                    _ => TariffZone.Evening
                };
            default:
                return TariffZone.All;
        }
    }

    public static bool IsFreeDay(DateTime date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return true;
        }

        if (FixedHolidays.Contains((date.Month, date.Day)))
        {
            return true;
        }

        // Easter Monday (Easter Sunday is a Sunday anyway).
        return date.Date == EasterSunday(date.Year).AddDays(1);
    }

    /// <summary>EU rule: summer time from the last Sunday of March to the last Sunday of October.</summary>
    public static bool IsSummerTime(DateTime local)
    {
        var start = LastSunday(local.Year, 3).AddHours(3);
        var end = LastSunday(local.Year, 10).AddHours(4);
        return local >= start && local < end;
    }

    private static DateTime LastSunday(int year, int month)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != DayOfWeek.Sunday)
        {
            d = d.AddDays(-1);
        }

        return d;
    }

    /// <summary>Gregorian Easter (anonymous algorithm).</summary>
    public static DateTime EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }

    /// <summary>Index into a 576 matrix: (month × 2 + free-day) × 24 + hour.</summary>
    public static int Cell576(DateTime when) => ((when.Month - 1) * 2 + (IsFreeDay(when) ? 1 : 0)) * 24 + when.Hour;

    public static int Cell576(int hourIndex) => (Month[hourIndex] * 2 + (FreeDay[hourIndex] ? 1 : 0)) * 24 + Dates[hourIndex].Hour;
}
