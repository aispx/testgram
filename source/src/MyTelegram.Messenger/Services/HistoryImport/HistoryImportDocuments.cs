using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Stage an import is in. See https://corefork.telegram.org/api/import
/// </summary>
public enum HistoryImportStatus
{
    /// <summary>Created by <c>messages.initHistoryImport</c>, still collecting media.</summary>
    Pending = 0,

    /// <summary><c>messages.startHistoryImport</c> was called, waiting for the background worker.</summary>
    Queued = 1,

    /// <summary>The worker is injecting the messages.</summary>
    Running = 2,

    Completed = 3,

    Failed = 4
}

/// <summary>
/// One import, stored in the MongoDB collection <c>history_imports</c>.
/// </summary>
public class HistoryImportDocument
{
    /// <summary>The <c>import_id</c> handed to the client in <c>messages.historyImport</c>.</summary>
    [BsonId]
    public long Id { get; set; }

    /// <summary>Importing user; every step of the flow must come from the same account.</summary>
    public long UserId { get; set; }

    public long PeerId { get; set; }

    public string PeerType { get; set; } = string.Empty;

    /// <summary>App the export file came from, for the logs.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>Media files the client announced in <c>initHistoryImport</c>.</summary>
    public int MediaCount { get; set; }

    public int TotalMessages { get; set; }

    public int ImportedCount { get; set; }

    public HistoryImportStatus Status { get; set; }

    /// <summary>Layer of the client that started the import, used when the messages are pushed.</summary>
    public int Layer { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Lease held by the worker, so two command servers cannot import the same file twice.</summary>
    public DateTime? ClaimedUntil { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// One parsed message of an import, stored in <c>history_import_messages</c>. Kept out of
/// <see cref="HistoryImportDocument"/> so a large export cannot hit the 16 MB document limit and so the
/// worker can stream the messages in batches.
/// </summary>
public class HistoryImportMessageDocument
{
    /// <summary><c>{ImportId}_{Seq}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long ImportId { get; set; }

    /// <summary>Position in the file; the messages are imported in this order.</summary>
    public int Seq { get; set; }

    /// <summary>Original send time, which ends up in <c>fwd_from.date</c>.</summary>
    public int Date { get; set; }

    /// <summary>Original sender, which ends up in <c>fwd_from.from_name</c>.</summary>
    public string FromName { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>Attachment the line refers to, matched against the uploaded media by name.</summary>
    public string? FileName { get; set; }

    /// <summary>Drives the TTL index that clears the leftovers of an abandoned import.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Media uploaded with <c>messages.uploadImportedMedia</c>, stored in <c>history_import_media</c>.
/// </summary>
public class HistoryImportMediaDocument
{
    /// <summary><c>{ImportId}_{FileName}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long ImportId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>The <c>MessageMedia</c> the media service produced, serialized as TL bytes.</summary>
    public byte[] Media { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
