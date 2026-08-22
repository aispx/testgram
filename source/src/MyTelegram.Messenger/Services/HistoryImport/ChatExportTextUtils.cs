using System.Globalization;
using System.Text;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Shared text and date helpers for the export dialects.
/// </summary>
/// <remarks>
/// Every exporter writes the timestamp in the locale of the phone that produced the file, so the
/// day/month order has to be recovered from the file itself rather than assumed.
/// </remarks>
internal static class ChatExportTextUtils
{
    /// <summary>Order of the two leading components of a numeric date.</summary>
    internal enum DateOrder
    {
        DayFirst,
        MonthFirst
    }

    /// <summary>Bidi marks and the byte order mark: invisible, and they break every regex.</summary>
    private static readonly char[] InvisibleChars =
    [
        '‎', '‏', '‪', '‫', '‬', '﻿'
    ];

    /// <summary>Spaces that are not <c>' '</c>, used around the AM/PM marker by the mobile exporters.</summary>
    private static readonly char[] ExoticSpaces = [' ', ' ', ' ', '　'];

    /// <summary>
    /// Removes the bidi marks and exotic spaces the mobile exporters sprinkle around timestamps, so a
    /// single regex can match both the Android and the iOS files.
    /// </summary>
    public static string Normalize(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (Array.IndexOf(InvisibleChars, c) >= 0)
            {
                continue;
            }

            builder.Append(Array.IndexOf(ExoticSpaces, c) >= 0 ? ' ' : c);
        }

        return builder.ToString().TrimEnd('\r', ' ', '\t');
    }

    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return [.. text.Split('\n').Select(Normalize)];
    }

    /// <summary>
    /// Decides whether the numeric dates of a file are written day first or month first: a component
    /// above twelve can only be a day, so the first unambiguous line settles it for the whole file.
    /// </summary>
    /// <param name="candidates">Pairs of the first two components of every date found in the file.</param>
    /// <param name="fallback">Order to use when every date in the file is ambiguous.</param>
    public static DateOrder ResolveOrder(IEnumerable<(int First, int Second)> candidates, DateOrder fallback)
    {
        foreach (var (first, second) in candidates)
        {
            if (first > 12 && second <= 12)
            {
                return DateOrder.DayFirst;
            }

            if (second > 12 && first <= 12)
            {
                return DateOrder.MonthFirst;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Turns the raw components of an export timestamp into unix seconds. Export files carry no time
    /// zone, so the wall clock time is read as UTC.
    /// </summary>
    public static int ToUnixSeconds(int year, int month, int day, int hour, int minute, int second)
    {
        if (year < 100)
        {
            year += year >= 70 ? 1900 : 2000;
        }

        if (year is < 1970 or > 2100 ||
            month is < 1 or > 12 ||
            day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59 ||
            second is < 0 or > 59)
        {
            throw new ChatExportDateException(
                $"Invalid date in the chat export file: {year:0000}-{month:00}-{day:00} {hour:00}:{minute:00}:{second:00}");
        }

        return (int)new DateTimeOffset(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
    }

    /// <summary>
    /// Applies the 12 hour marker of a timestamp, accepting the localised forms the exporters use.
    /// </summary>
    public static int ApplyMeridiem(int hour, string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return hour;
        }

        var isPm = marker.Contains('p', StringComparison.OrdinalIgnoreCase) ||
                   marker.Contains("오후", StringComparison.Ordinal) ||
                   marker.Contains("午後", StringComparison.Ordinal);
        var isAm = marker.Contains('a', StringComparison.OrdinalIgnoreCase) ||
                   marker.Contains("오전", StringComparison.Ordinal) ||
                   marker.Contains("午前", StringComparison.Ordinal);

        if (isPm && hour < 12)
        {
            return hour + 12;
        }

        if (isAm && hour == 12)
        {
            return 0;
        }

        return hour;
    }

    public static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// A chat with three or more distinct participants cannot be a private chat. Two names are
    /// inconclusive on their own, so the caller combines this with whatever the header said.
    /// </summary>
    public static bool LooksLikeGroup(IEnumerable<string> senderNames)
    {
        return senderNames.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).Count() >= 3;
    }

    /// <summary>Reads the quoted title out of a system line such as <c>created group "Family"</c>.</summary>
    public static string? ExtractQuotedTitle(string line)
    {
        (char Open, char Close)[] quotePairs =
        [
            ('"', '"'), ('“', '”'), ('«', '»'), ('‘', '’'), ('\'', '\'')
        ];

        foreach (var (open, close) in quotePairs)
        {
            var start = line.IndexOf(open);
            if (start < 0)
            {
                continue;
            }

            var end = line.IndexOf(close, start + 1);
            if (end > start + 1)
            {
                return line[(start + 1)..end].Trim();
            }
        }

        return null;
    }
}
