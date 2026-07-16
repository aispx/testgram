using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// Persistence document for an issued async graph token in the <c>stats_async_graph</c> collection.
/// </summary>
public sealed class StatsAsyncGraphDocument
{
    /// <summary>The opaque random token; also the document <c>_id</c>.</summary>
    [BsonId]
    public string Token { get; set; } = string.Empty;

    /// <summary>The statistics snapshot the graph belongs to (used for currency checks).</summary>
    public string SnapshotId { get; set; } = string.Empty;

    /// <summary>The serialized main <see cref="GraphSpec"/> (JSON).</summary>
    public string SpecJson { get; set; } = string.Empty;

    /// <summary>The serialized zoomed <see cref="GraphSpec"/> (JSON), or <c>null</c> when there is no zoom series.</summary>
    public string? ZoomJson { get; set; }

    /// <summary>The theme captured when the token was issued.</summary>
    public bool Dark { get; set; }

    /// <summary>The issue time as a Unix-second timestamp; the validity window is 86,400 seconds.</summary>
    public int IssuedAt { get; set; }

    /// <summary>The absolute expiry instant, backing the TTL index.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Persistence document tracking the current snapshot id for a snapshot scope in the
/// <c>stats_async_graph_current</c> collection. Used to detect outdated tokens (Requirement 9.6).
/// </summary>
public sealed class StatsAsyncGraphSnapshotDocument
{
    /// <summary>The snapshot scope; also the document <c>_id</c>.</summary>
    [BsonId]
    public string Scope { get; set; } = string.Empty;

    /// <summary>The snapshot id currently considered live for the scope.</summary>
    public string CurrentSnapshotId { get; set; } = string.Empty;
}
