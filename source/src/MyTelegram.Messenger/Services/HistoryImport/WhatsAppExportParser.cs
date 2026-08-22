using System.Text;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads the plain text chat export of WhatsApp, both the Android flavour
/// (<c>12/31/20, 23:59 - John: text</c>) and the iOS flavour
/// (<c>[31/12/2020, 23:59:59] John: text</c>).
/// See https://corefork.telegram.org/api/import
/// </summary>
internal sealed partial class WhatsAppExportParser : IChatExportFormatParser
{
    private const string TimePattern =
        @"(?<h>\d{1,2}):(?<mi>\d{2})(?::(?<s>\d{2}))?\s*(?<ap>[APap]\.?\s?[Mm]\.?)?";

    private const string DatePattern = @"(?<d1>\d{1,4})[./\-](?<d2>\d{1,2})[./\-](?<d3>\d{2,4})";

    public ChatExportFormat Format => ChatExportFormat.WhatsApp;

    /// <summary>Android: the timestamp is followed by " - ".</summary>
    [GeneratedRegex($@"^{DatePattern},?\s+{TimePattern}\s+-\s+(?<rest>.*)$")]
    private static partial Regex AndroidLineRegex();

    /// <summary>iOS: the timestamp is wrapped in brackets.</summary>
    [GeneratedRegex($@"^\[{DatePattern},?\s+{TimePattern}\]\s*(?<rest>.*)$")]
    private static partial Regex IosLineRegex();

    /// <summary><c>Name: text</c>. A line without it is a system notice, not a message.</summary>
    [GeneratedRegex(@"^(?<name>[^:]{1,100}?):\s(?<text>[\s\S]*)$")]
    private static partial Regex SenderRegex();

    /// <summary>iOS attachment: <c>&lt;attached: 00000042-PHOTO-2020-12-31-23-59-59.jpg&gt;</c>.</summary>
    [GeneratedRegex(@"<attached:\s*(?<file>[^>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex IosAttachmentRegex();

    /// <summary>Android attachment: <c>IMG-20201231-WA0001.jpg (file attached)</c>.</summary>
    [GeneratedRegex(@"^(?<file>\S[^\r\n]*?)\s*\((?:file attached|archivo adjunto|Datei angehängt|fichier joint|arquivo anexado|файл прикреплён)\)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AndroidAttachmentRegex();

    public int Detect(IReadOnlyList<string> lines)
    {
        var score = 0;
        foreach (var line in lines)
        {
            if (TryMatchTimestamp(line, out _))
            {
                score++;
            }
        }

        return score;
    }

    public ChatExportHead ReadHead(IReadOnlyList<string> lines)
    {
        // The head is a truncated file, so a half written last line must not fail the whole check.
        var (messages, systemLines) = ReadCore(lines, tolerateDateErrors: true);

        var isGroup = false;
        string? title = null;

        foreach (var systemLine in systemLines)
        {
            var groupTitle = TryReadCreatedGroupTitle(systemLine);
            if (groupTitle != null)
            {
                isGroup = true;
                title ??= groupTitle;
                continue;
            }

            if (LooksLikeGroupSystemLine(systemLine))
            {
                isGroup = true;
            }
        }

        if (!isGroup && ChatExportTextUtils.LooksLikeGroup(messages.Select(p => p.FromName)))
        {
            isGroup = true;
        }

        // WhatsApp never writes the name of the other party into a one to one export, so a private
        // chat has no title to report.
        return new ChatExportHead(Format, !isGroup, isGroup, title);
    }

    public IReadOnlyList<ImportedMessageLine> ReadMessages(IReadOnlyList<string> lines)
    {
        return ReadCore(lines, tolerateDateErrors: false).Messages;
    }

    private (List<ImportedMessageLine> Messages, List<string> SystemLines) ReadCore(IReadOnlyList<string> lines,
        bool tolerateDateErrors)
    {
        var order = ChatExportTextUtils.ResolveOrder(CollectDateCandidates(lines),
            // The dash flavour comes from the US date locale far more often than not; the bracket
            // flavour is the international one.
            HasBracketedLines(lines) ? ChatExportTextUtils.DateOrder.DayFirst : ChatExportTextUtils.DateOrder.MonthFirst);

        var messages = new List<ImportedMessageLine>();
        var systemLines = new List<string>();
        var pendingText = new StringBuilder();
        ImportedMessageLine? pending = null;

        foreach (var line in lines)
        {
            if (!TryMatchTimestamp(line, out var match))
            {
                // A message that spans several lines keeps the timestamp only on its first line.
                if (pending != null && line.Length > 0)
                {
                    pendingText.Append('\n').Append(line);
                }

                continue;
            }

            Flush(messages, ref pending, pendingText);

            int date;
            try
            {
                date = BuildDate(match!, order);
            }
            catch (ChatExportDateException)
            {
                if (!tolerateDateErrors)
                {
                    throw;
                }

                continue;
            }

            var rest = match!.Groups["rest"].Value.Trim();
            var senderMatch = SenderRegex().Match(rest);
            if (!senderMatch.Success)
            {
                systemLines.Add(rest);
                continue;
            }

            var name = senderMatch.Groups["name"].Value.Trim();
            var text = senderMatch.Groups["text"].Value;
            var (cleanText, fileName) = ExtractAttachment(text);

            pending = new ImportedMessageLine(date, name, cleanText, fileName);
            pendingText.Clear().Append(cleanText);
        }

        Flush(messages, ref pending, pendingText);

        return (messages, systemLines);
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

        // Nothing to import: the export dropped the media and left no text behind.
        if (completed.Text.Length == 0 && completed.FileName == null)
        {
            return;
        }

        messages.Add(completed);
    }

    private static bool HasBracketedLines(IReadOnlyList<string> lines)
    {
        return lines.Any(p => IosLineRegex().IsMatch(p));
    }

    private static IEnumerable<(int First, int Second)> CollectDateCandidates(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!TryMatchTimestamp(line, out var match))
            {
                continue;
            }

            var d1 = match!.Groups["d1"].Value;
            if (d1.Length == 4)
            {
                // Year first, nothing to disambiguate.
                continue;
            }

            yield return (ChatExportTextUtils.ParseInt(d1), ChatExportTextUtils.ParseInt(match.Groups["d2"].Value));
        }
    }

    private static bool TryMatchTimestamp(string line, out Match? match)
    {
        var ios = IosLineRegex().Match(line);
        if (ios.Success)
        {
            match = ios;
            return true;
        }

        var android = AndroidLineRegex().Match(line);
        if (android.Success)
        {
            match = android;
            return true;
        }

        match = null;
        return false;
    }

    private static int BuildDate(Match match, ChatExportTextUtils.DateOrder order)
    {
        var d1 = ChatExportTextUtils.ParseInt(match.Groups["d1"].Value);
        var d2 = ChatExportTextUtils.ParseInt(match.Groups["d2"].Value);
        var d3 = ChatExportTextUtils.ParseInt(match.Groups["d3"].Value);

        int year, month, day;
        if (match.Groups["d1"].Value.Length == 4)
        {
            (year, month, day) = (d1, d2, d3);
        }
        else
        {
            year = d3;
            (day, month) = order == ChatExportTextUtils.DateOrder.DayFirst ? (d1, d2) : (d2, d1);
        }

        var hour = ChatExportTextUtils.ApplyMeridiem(ChatExportTextUtils.ParseInt(match.Groups["h"].Value),
            match.Groups["ap"].Success ? match.Groups["ap"].Value : null);
        var minute = ChatExportTextUtils.ParseInt(match.Groups["mi"].Value);
        var second = match.Groups["s"].Success ? ChatExportTextUtils.ParseInt(match.Groups["s"].Value) : 0;

        return ChatExportTextUtils.ToUnixSeconds(year, month, day, hour, minute, second);
    }

    private static (string Text, string? FileName) ExtractAttachment(string text)
    {
        var trimmed = text.Trim();

        var ios = IosAttachmentRegex().Match(trimmed);
        if (ios.Success)
        {
            var withoutToken = trimmed.Remove(ios.Index, ios.Length).Trim();
            return (withoutToken, ios.Groups["file"].Value.Trim());
        }

        var android = AndroidAttachmentRegex().Match(trimmed);
        if (android.Success)
        {
            return (string.Empty, android.Groups["file"].Value.Trim());
        }

        // A placeholder the exporter left behind for media it did not export ("<Media omitted>",
        // "<Без медиафайлов>", one per language) is imported verbatim, as the official server does:
        // the line is part of the history and its wording cannot be enumerated for every locale.
        return (trimmed, null);
    }

    private static string? TryReadCreatedGroupTitle(string systemLine)
    {
        const string marker = "created group";
        var index = systemLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var quoted = ChatExportTextUtils.ExtractQuotedTitle(systemLine);
        if (!string.IsNullOrWhiteSpace(quoted))
        {
            return quoted;
        }

        var rest = systemLine[(index + marker.Length)..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private static bool LooksLikeGroupSystemLine(string systemLine)
    {
        string[] markers =
        [
            "added you", "were added", "joined using this group", "group's invite link",
            "changed the subject", "changed this group's icon", "left", "removed"
        ];

        return markers.Any(marker => systemLine.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
