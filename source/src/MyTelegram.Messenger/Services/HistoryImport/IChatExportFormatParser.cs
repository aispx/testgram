namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Reader for one chat export dialect. See https://corefork.telegram.org/api/import
/// </summary>
internal interface IChatExportFormatParser
{
    ChatExportFormat Format { get; }

    /// <summary>
    /// How strongly the given lines look like this dialect; zero means "not this format". The
    /// aggregate parser picks the highest score, so a file that superficially matches two dialects
    /// still lands on the one that actually reads its message lines.
    /// </summary>
    int Detect(IReadOnlyList<string> lines);

    /// <summary>Reads what the file says about the chat itself.</summary>
    ChatExportHead ReadHead(IReadOnlyList<string> lines);

    /// <summary>Reads every message of the file, in file order.</summary>
    IReadOnlyList<ImportedMessageLine> ReadMessages(IReadOnlyList<string> lines);
}
