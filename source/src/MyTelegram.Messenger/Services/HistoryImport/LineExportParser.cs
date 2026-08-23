using System.Text;
using System.Text.RegularExpressions;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads the plain text chat export of LINE, which writes a header, then one block per day
/// (<c>2020/12/31(Thu)</c>) and tab separated message lines (<c>23:59\tJohn\ttext</c>).
/// See https://corefork.telegram.org/api/import
/// </summary>
internal sealed partial class LineExportParser : IChatExportFormatParser
{
    public ChatExportFormat Format => ChatExportFormat.Line;

    /// <summary>Day header: <c>2020/12/31(Thu)</c>, <c>2020.12.31 Thursday</c>, <c>2020/12/31(木)</c>.</summary>
    [GeneratedRegex(@"^(?<y>\d{4})[./\-](?<m>\d{1,2})[./\-](?<d>\d{1,2})\s*(?:\(.*\)|[\p{L}]+)?$")]
    private static partial Regex DayHeaderRegex();

    /// <summary>Message: <c>23:59\tJohn\ttext</c>, with an optional AM/PM marker.</summary>
    [GeneratedRegex("^(?<h>\\d{1,2}):(?<mi>\\d{2})\\s*(?<ap>[APap]\\.?[Mm]\\.?)?\t(?<name>[^\t]*)\t(?<text>[\\s\\S]*)$")]
    private static partial Regex MessageRegex();

    /// <summary>Header: <c>[LINE] Chat with John</c> / <c>[LINE] Chat history with Family</c>.</summary>
    [GeneratedRegex(@"^\[LINE\]\s*(?<kind>Chat history in|Chat history with|Chat in|Chat with)?\s*(?<title>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();

    /// <summary>Attachment token: <c>[File] report.pdf</c>, <c>[Photo]</c>, <c>[Sticker]</c>.</summary>
    [GeneratedRegex(@"^\[(?<kind>Photo|Video|Sticker|File|Voice message|Contact|Album|Note|Gift|Location|写真|動画|スタンプ|ファイル|ボイスメッセージ)\]\s*(?<file>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachmentRegex();

    public int Detect(IReadOnlyList<string> lines)
    {
        var score = lines.Count(p => MessageRegex().IsMatch(p));
        if (score == 0)
        {
            return 0;
        }

        // The header and the day blocks are what tells LINE apart from a random tab separated file.
        if (lines.Any(p => p.StartsWith("[LINE]", StringComparison.OrdinalIgnoreCase)))
        {
            score += 10;
        }

        if (lines.Any(p => DayHeaderRegex().IsMatch(p)))
        {
            score += 5;
        }

        return score;
    }

    public ChatExportHead ReadHead(IReadOnlyList<string> lines)
    {
        var messages = ReadCore(lines, tolerateDateErrors: true);
        var (title, headerSaysGroup) = ReadHeader(lines);

        var isGroup = headerSaysGroup || ChatExportTextUtils.LooksLikeGroup(messages.Select(p => p.FromName));

        return new ChatExportHead(Format, !isGroup, isGroup, title);
    }

    public IReadOnlyList<ImportedMessageLine> ReadMessages(IReadOnlyList<string> lines)
    {
        return ReadCore(lines, tolerateDateErrors: false);
    }

    private static (string? Title, bool IsGroup) ReadHeader(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(5))
        {
            var match = HeaderRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value;
            var title = match.Groups["title"].Value.Trim();

            // "Chat in <name>" and "Chat history in <name>" are the group wordings; "with" is a
            // one to one chat.
            var isGroup = kind.EndsWith("in", StringComparison.OrdinalIgnoreCase);

            return (title.Length == 0 ? null : title, isGroup);
        }

        return (null, false);
    }

    private static List<ImportedMessageLine> ReadCore(IReadOnlyList<string> lines, bool tolerateDateErrors)
    {
        var messages = new List<ImportedMessageLine>();
        var pendingText = new StringBuilder();
        ImportedMessageLine? pending = null;
        (int Year, int Month, int Day)? day = null;

        foreach (var line in lines)
        {
            var dayHeader = DayHeaderRegex().Match(line);
            if (dayHeader.Success)
            {
                Flush(messages, ref pending, pendingText);
                day = (ChatExportTextUtils.ParseInt(dayHeader.Groups["y"].Value),
                    ChatExportTextUtils.ParseInt(dayHeader.Groups["m"].Value),
                    ChatExportTextUtils.ParseInt(dayHeader.Groups["d"].Value));
                continue;
            }

            var message = MessageRegex().Match(line);
            if (!message.Success)
            {
                // Continuation of a multi line message; LINE indents those with a tab.
                if (pending != null && line.Trim().Length > 0)
                {
                    pendingText.Append('\n').Append(line.TrimStart('\t'));
                }

                continue;
            }

            Flush(messages, ref pending, pendingText);

            if (day == null)
            {
                // A message before the first day block has no date at all.
                if (!tolerateDateErrors)
                {
                    throw new ChatExportDateException("A LINE export message appears before any day header");
                }

                continue;
            }

            int date;
            try
            {
                var hour = ChatExportTextUtils.ApplyMeridiem(
                    ChatExportTextUtils.ParseInt(message.Groups["h"].Value),
                    message.Groups["ap"].Success ? message.Groups["ap"].Value : null);
                date = ChatExportTextUtils.ToUnixSeconds(day.Value.Year, day.Value.Month, day.Value.Day, hour,
                    ChatExportTextUtils.ParseInt(message.Groups["mi"].Value), 0);
            }
            catch (ChatExportDateException)
            {
                if (!tolerateDateErrors)
                {
                    throw;
                }

                continue;
            }

            var name = message.Groups["name"].Value.Trim();
            if (name.Length == 0)
            {
                // A line with no author is a system notice ("X joined the chat").
                continue;
            }

            var (text, fileName) = ExtractAttachment(message.Groups["text"].Value.Trim());
            pending = new ImportedMessageLine(date, name, text, fileName);
            pendingText.Clear().Append(text);
        }

        Flush(messages, ref pending, pendingText);

        return messages;
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
        var match = AttachmentRegex().Match(text);
        if (!match.Success)
        {
            return (text, null);
        }

        // "[File] report.pdf" names the attachment and the media replaces the line; "[Photo]" alone
        // names nothing, so the placeholder is imported verbatim like any other text.
        var file = match.Groups["file"].Value.Trim();

        return file.Length == 0 ? (text, null) : (string.Empty, file);
    }
}
