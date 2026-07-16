using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stats;

/// <summary>
/// One document per recorded public forward in the <c>stats_public_forward</c> collection.
/// </summary>
public class PublicForwardDocument
{
    /// <summary>
    /// Dedupe key: <c>"{sourceType}:{ownerPeerId}:{itemId}:{fwdPeerId}:{fwdMsgId}"</c>.
    /// </summary>
    public string Id { get; set; } = null!;

    [BsonRepresentation(BsonType.Int32)]
    public ForwardSourceType SourceType { get; set; }

    public long SourceOwnerPeerId { get; set; }

    public long SourceItemId { get; set; }

    /// <summary>The public channel/chat (with a username) that forwarded the source.</summary>
    public long ForwardingPeerId { get; set; }

    /// <summary>The message id of the forward inside the forwarding peer.</summary>
    public int ForwardingMsgId { get; set; }

    /// <summary>Deterministic total-ordering key used as an opaque paging cursor.</summary>
    public long OrderKey { get; set; }

    /// <summary>Soft-delete flag; removed forwards are excluded from count and pages.</summary>
    public bool Removed { get; set; }
}
