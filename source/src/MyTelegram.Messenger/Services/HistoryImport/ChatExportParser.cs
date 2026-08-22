namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reads a chat export file produced by a foreign chat app.
/// See https://corefork.telegram.org/api/import
/// </summary>
public interface IChatExportParser
{
    /// <summary>
    /// Reads the first lines of an export file, as passed to <c>messages.checkHistoryImport</c>.
    /// Returns null when no supported app produced the file.
    /// </summary>
    ChatExportHead? ParseHead(string importHead);

    /// <summary>
    /// Reads a whole export file. Returns null when no supported app produced the file, and throws
    /// <see cref="ChatExportDateException"/> when a message carries a timestamp that cannot be read.
    /// </summary>
    ChatExportParseResult? Parse(string text);
}

/// <inheritdoc />
public class ChatExportParser : IChatExportParser, ITransientDependency
{
    /// <summary>The clients send the first 100 lines of the file as <c>import_head</c>.</summary>
    private const int HeadLineCount = 100;

    /// <summary>Lines examined when deciding which app produced the file.</summary>
    private const int DetectionLineCount = 200;

    private static readonly IChatExportFormatParser[] Parsers =
    [
        new WhatsAppExportParser(),
        new LineExportParser(),
        new KakaoTalkExportParser()
    ];

    public ChatExportHead? ParseHead(string importHead)
    {
        var lines = ChatExportTextUtils.SplitLines(importHead);
        var parser = Detect(lines);

        return parser?.ReadHead(lines);
    }

    public ChatExportParseResult? Parse(string text)
    {
        var lines = ChatExportTextUtils.SplitLines(text);
        var parser = Detect(lines);
        if (parser == null)
        {
            return null;
        }

        // The head flags come from the same lines the client sent to checkHistoryImport, so the
        // answer of the two methods cannot disagree.
        var head = parser.ReadHead([.. lines.Take(HeadLineCount)]);
        var messages = parser.ReadMessages(lines);

        return new ChatExportParseResult(parser.Format, head.IsPm, head.IsGroup, head.Title, messages);
    }

    private static IChatExportFormatParser? Detect(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        // Every dialect is recognizable from the top of the file, and a 32 MB export would otherwise be
        // scanned once per dialect before the real parsing even starts.
        var window = lines.Count <= DetectionLineCount ? lines : [.. lines.Take(DetectionLineCount)];

        IChatExportFormatParser? best = null;
        var bestScore = 0;

        foreach (var parser in Parsers)
        {
            var score = parser.Detect(window);
            if (score > bestScore)
            {
                best = parser;
                bestScore = score;
            }
        }

        return best;
    }
}
