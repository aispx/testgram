using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Tracks an approved suggested post until the funds become "settled" (i.e. age_min seconds
/// have passed since the approval), at which point a messageActionSuggestedPostSuccess
/// is emitted into the monoforum.
/// </summary>
public class SuggestedPostSettlementDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = string.Empty;

    /// <summary>The monoforum (saved-messages-style) channel where the original suggestion lives.</summary>
    public long MonoforumChannelId { get; set; }
    /// <summary>The broadcast channel (revenue receiver).</summary>
    public long BroadcastChannelId { get; set; }
    /// <summary>The original suggested-post message id inside the monoforum.</summary>
    public int OriginalMsgId { get; set; }
    /// <summary>Author of the suggested post (the one who pays).</summary>
    public long AuthorUserId { get; set; }
    /// <summary>User who approved (channel admin/owner).</summary>
    public long ApproverUserId { get; set; }

    /// <summary>"stars" or "ton".</summary>
    public string Currency { get; set; } = "stars";
    /// <summary>Price in stars or nanotons.</summary>
    public long Amount { get; set; }

    public int ApprovedAt { get; set; }
    /// <summary>Unix-time after which the success action should be emitted.</summary>
    public int SettleAt { get; set; }

    /// <summary>"pending" | "success" | "refunded".</summary>
    public string Status { get; set; } = "pending";
}
