using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads the plain text chat export of KakaoTalk: a header, day separators
/// (<c>--------------- Thursday, December 31, 2020 ---------------</c>) and either the mobile message
/// form (<c>[John] [11:59 PM] text</c>) or the desktop one
/// (<c>2020년 12월 31일 오후 11:59, John : text</c>).
/// See https://corefork.telegram.org/api/import
/// </summary>
internal sealed partial class KakaoTalkExportParser : IChatExportFormatParser
{
    private static readonly string[] DaySeparatorFormats =
    [
        "dddd, MMMM d, yyyy", "MMMM d, yyyy", "dddd, d MMMM yyyy", "d MMMM yyyy", "yyyy-MM-dd", "yyyy/MM/dd"
    ];

    public ChatExportFormat Format => ChatExportFormat.KakaoTalk;

    /// <summary>Day separator, dashes on both sides of the date.</summary>
    [GeneratedRegex(@"^-{3,}\s*(?<date>.+?)\s*-{3,}$")]
    private static partial Regex DaySeparatorRegex();

    /// <summary>Korean date, used both in the separator and in the desktop message line.</summary>
    [GeneratedRegex(@"(?<y>\d{4})년\s*(?<m>\d{1,2})월\s*(?<d>\d{1,2})일")]
    private static partial Regex KoreanDateRegex();

    /// <summary>Mobile message: <c>[John] [11:59 PM] text</c> / <c>[John] [오후 11:59] text</c>.</summary>
    [GeneratedRegex(@"^\[(?<name>[^\]]{1,100})\]\s*\[(?<time>[^\]]{1,30})\]\s*(?<text>[\s\S]*)$")]
    private static partial Regex MobileMessageRegex();

    /// <summary>Desktop message: <c>2020년 12월 31일 오후 11:59, John : text</c>.</summary>
    [GeneratedRegex(@"^(?<y>\d{4})년\s*(?<m>\d{1,2})월\s*(?<d>\d{1,2})일\s*(?<ap>오전|오후)?\s*(?<h>\d{1,2}):(?<mi>\d{2}),\s*(?<name>.+?)\s:\s(?<text>[\s\S]*)$")]
    private static partial Regex DesktopMessageRegex();

    /// <summary>Time inside the brackets of a mobile line.</summary>
    [GeneratedRegex(@"^(?<ap1>오전|오후|AM|PM)?\s*(?<h>\d{1,2}):(?<mi>\d{2})\s*(?<ap2>[APap]\.?[Mm]\.?)?$")]
    private static partial Regex TimeRegex();

    /// <summary>Header naming the chat: <c>Chat with John</c> / <c>John 님과 카카오톡 대화</c>.</summary>
    [GeneratedRegex(@"^(?:Chat with\s+(?<en>.+)|(?<kr>.+?)\s*님과(?:의)?\s*카카오톡\s*대화)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();

    /// <summary>Attachment that names the file: <c>File: report.pdf</c>.</summary>
    [GeneratedRegex(@"^(?:File|Photo|Video|Audio|파일|사진|동영상)\s*:\s*(?<file>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NamedAttachmentRegex();

    public int Detect(IReadOnlyList<string> lines)
    {
        var score = lines.Count(p => MobileMessageRegex().IsMatch(p) || DesktopMessageRegex().IsMatch(p));
        if (score == 0)
        {
            return 0;
        }

        if (lines.Any(p => DaySeparatorRegex().IsMatch(p)))
        {
            score += 5;
        }

        if (lines.Any(p => p.Contains("카카오톡", StringComparison.Ordinal) ||
                           p.StartsWith("Date Saved", StringComparison.OrdinalIgnoreCase) ||
                           p.StartsWith("Saved Date", StringComparison.OrdinalIgnoreCase) ||
                           p.StartsWith("저장한 날짜", StringComparison.Ordinal)))
        {
            score += 10;
        }

        return score;
    }

    public ChatExportHead ReadHead(IReadOnlyList<string> lines)
    {
        var messages = ReadCore(lines, tolerateDateErrors: true);
        var title = ReadTitle(lines);
        var isGroup = ChatExportTextUtils.LooksLikeGroup(messages.Select(p => p.FromName));

        return new ChatExportHead(Format, !isGroup, isGroup, title);
    }

    public IReadOnlyList<ImportedMessageLine> ReadMessages(IReadOnlyList<string> lines)
    {
        return ReadCore(lines, tolerateDateErrors: false);
    }

    private static string? ReadTitle(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(5))
        {
            var match = HeaderRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var title = (match.Groups["en"].Success ? match.Groups["en"].Value : match.Groups["kr"].Value).Trim();
            if (title.Length > 0)
            {
                return title;
            }
        }

        return null;
    }

    private static List<ImportedMessageLine> ReadCore(IReadOnlyList<string> lines, bool tolerateDateErrors)
    {
        var messages = new List<ImportedMessageLine>();
        var pendingText = new StringBuilder();
        ImportedMessageLine? pending = null;
        (int Year, int Month, int Day)? day = null;

        foreach (var line in lines)
        {
            var separator = DaySeparatorRegex().Match(line);
            if (separator.Success)
            {
                Flush(messages, ref pending, pendingText);
                day = TryReadDay(separator.Groups["date"].Value) ?? day;
                continue;
            }

            var desktop = DesktopMessageRegex().Match(line);
            if (desktop.Success)
            {
                Flush(messages, ref pending, pendingText);
                if (!TryBuildDesktopMessage(desktop, tolerateDateErrors, out pending))
                {
                    continue;
                }

                pendingText.Clear().Append(pending!.Text);
                continue;
            }

            var mobile = MobileMessageRegex().Match(line);
            if (!mobile.Success)
            {
                if (pending != null && line.Trim().Length > 0)
                {
                    pendingText.Append('\n').Append(line);
                }

                continue;
            }

            Flush(messages, ref pending, pendingText);

            if (day == null)
            {
                if (!tolerateDateErrors)
                {
                    throw new ChatExportDateException(
                        "A KakaoTalk export message appears before any day separator");
                }

                continue;
            }

            var time = TimeRegex().Match(mobile.Groups["time"].Value.Trim());
            if (!time.Success)
            {
                if (!tolerateDateErrors)
                {
                    throw new ChatExportDateException(
                        $"Unrecognized time in the chat export file: {mobile.Groups["time"].Value}");
                }

                continue;
            }

            int date;
            try
            {
                var marker = time.Groups["ap1"].Success ? time.Groups["ap1"].Value :
                    time.Groups["ap2"].Success ? time.Groups["ap2"].Value : null;
                var hour = ChatExportTextUtils.ApplyMeridiem(
                    ChatExportTextUtils.ParseInt(time.Groups["h"].Value), marker);
                date = ChatExportTextUtils.ToUnixSeconds(day.Value.Year, day.Value.Month, day.Value.Day, hour,
                    ChatExportTextUtils.ParseInt(time.Groups["mi"].Value), 0);
            }
            catch (ChatExportDateException)
            {
                if (!tolerateDateErrors)
                {
                    throw;
                }

                continue;
            }

            var (text, fileName) = ExtractAttachment(mobile.Groups["text"].Value.Trim());
            pending = new ImportedMessageLine(date, mobile.Groups["name"].Value.Trim(), text, fileName);
            pendingText.Clear().Append(text);
        }

        Flush(messages, ref pending, pendingText);

        return messages;
    }

    private static bool TryBuildDesktopMessage(Match match, bool tolerateDateErrors, out ImportedMessageLine? message)
    {
        message = null;
        int date;
        try
        {
            var hour = ChatExportTextUtils.ApplyMeridiem(ChatExportTextUtils.ParseInt(match.Groups["h"].Value),
                match.Groups["ap"].Success ? match.Groups["ap"].Value : null);
            date = ChatExportTextUtils.ToUnixSeconds(
                ChatExportTextUtils.ParseInt(match.Groups["y"].Value),
                ChatExportTextUtils.ParseInt(match.Groups["m"].Value),
                ChatExportTextUtils.ParseInt(match.Groups["d"].Value),
                hour,
                ChatExportTextUtils.ParseInt(match.Groups["mi"].Value),
                0);
        }
        catch (ChatExportDateException)
        {
            if (!tolerateDateErrors)
            {
                throw;
            }

            return false;
        }

        var (text, fileName) = ExtractAttachment(match.Groups["text"].Value.Trim());
        message = new ImportedMessageLine(date, match.Groups["name"].Value.Trim(), text, fileName);

        return true;
    }

    private static (int Year, int Month, int Day)? TryReadDay(string value)
    {
        var korean = KoreanDateRegex().Match(value);
        if (korean.Success)
        {
            return (ChatExportTextUtils.ParseInt(korean.Groups["y"].Value),
                ChatExportTextUtils.ParseInt(korean.Groups["m"].Value),
                ChatExportTextUtils.ParseInt(korean.Groups["d"].Value));
        }

        if (DateTime.TryParseExact(value, DaySeparatorFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return (parsed.Year, parsed.Month, parsed.Day);
        }

        return null;
    }

    private static void Flush(List<ImportedMessageLine> messages, ref ImportedMessageLine? pending,
        StringBuilder pendingText)
    {
        if (pending == null)
        {
            return;
        }

        var completed = pending with { Text = pendingText.ToString().Trim() };
        pending = null;
        pendingText.Clear();

        if (completed.Text.Length == 0 && completed.FileName == null)
        {
            return;
        }

        messages.Add(completed);
    }

    private static (string Text, string? FileName) ExtractAttachment(string text)
    {
        // "File: report.pdf" names the attachment and the media replaces the line; a bare "Photo"
        // names nothing, so the placeholder is imported verbatim like any other text.
        var named = NamedAttachmentRegex().Match(text);

        return named.Success ? (string.Empty, named.Groups["file"].Value.Trim()) : (text, null);
    }
}
