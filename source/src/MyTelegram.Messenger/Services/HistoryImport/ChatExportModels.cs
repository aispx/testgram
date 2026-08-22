namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Chat app a history export file was produced by.
/// See https://corefork.telegram.org/api/import
/// </summary>
public enum ChatExportFormat
{
    Unknown = 0,
    WhatsApp = 1,
    Line = 2,
    KakaoTalk = 3
}

/// <summary>
/// What the first lines of an export file say about the exported chat, which is all
/// <c>messages.checkHistoryImport</c> is allowed to answer.
/// </summary>
/// <param name="Format">The app the export came from.</param>
/// <param name="IsPm">The export is a one to one chat.</param>
/// <param name="IsGroup">The export is a group chat.</param>
/// <param name="Title">Title of the exported chat, when the file names it.</param>
public record ChatExportHead(ChatExportFormat Format, bool IsPm, bool IsGroup, string? Title);

/// <summary>
/// One message read out of an export file.
/// </summary>
/// <param name="Date">Original send time, unix seconds.</param>
/// <param name="FromName">Name of the original sender, as written in the export.</param>
/// <param name="Text">Message text, already stripped of the media placeholder.</param>
/// <param name="FileName">
/// Name of the attachment the line refers to, matched later against the <c>file_name</c> of
/// <c>messages.uploadImportedMedia</c>. Null when the message has no attachment or when the export
/// only left an untitled placeholder behind.
/// </param>
public record ImportedMessageLine(int Date, string FromName, string Text, string? FileName);

/// <summary>
/// Full result of parsing an export file.
/// </summary>
public record ChatExportParseResult(
    ChatExportFormat Format,
    bool IsPm,
    bool IsGroup,
    string? Title,
    IReadOnlyList<ImportedMessageLine> Messages);

/// <summary>
/// Raised when a line carries a timestamp the parser recognises the shape of but cannot turn into a
/// date, which the API reports as <c>IMPORT_FORMAT_DATE_INVALID</c>.
/// </summary>
public class ChatExportDateException(string message) : Exception(message);
