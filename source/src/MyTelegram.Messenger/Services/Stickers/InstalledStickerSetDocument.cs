using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// One stickerset installed by one user.
///
/// <para>This is a plain collection rather than an <c>eventflow-*</c> read model on purpose: it is a
/// per-user list with no domain invariants worth an aggregate, exactly like <c>saved_gifs</c>,
/// <c>recent_stickers</c> and <c>faved_stickers</c>. Earlier code wrote straight into
/// <c>eventflow-userinstalledstickersetreadmodel</c>, which the project forbids;
/// <c>scripts/migrate_installed_sticker_sets.py</c> moves those rows here.</para>
/// </summary>
[BsonIgnoreExtraElements]
public class InstalledStickerSetDocument
{
    /// <summary><c>{UserId}:{StickerSetId}</c>, so installing twice is an upsert.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public long UserId { get; set; }

    public long StickerSetId { get; set; }

    /// <summary>
    /// Which of the three lists this set belongs to. Clients keep normal stickers, masks and custom
    /// emoji in separate tabs and fetch them with separate methods
    /// (<c>getAllStickers</c> / <c>getMaskStickers</c> / <c>getEmojiStickers</c>), so the type has to
    /// be stored: the caller must not have to load the catalogue row just to decide whether a set
    /// belongs in the answer.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public StickerSetType StickerSetType { get; set; } = StickerSetType.Regular;

    /// <summary>
    /// An archived set stays installed but is hidden from the panel — it is returned by
    /// <c>messages.getArchivedStickers</c> instead of <c>messages.getAllStickers</c>.
    /// </summary>
    public bool Archived { get; set; }

    /// <summary>
    /// Strictly increasing per user; lists are returned in descending <c>Order</c>, so the newest
    /// install is first and <c>messages.reorderStickerSets</c> just rewrites these numbers. Clients
    /// adopt the order verbatim, and the hash they send is computed over the list in that order.
    /// </summary>
    public long Order { get; set; }

    /// <summary>Unixtime of the install, surfaced as <c>stickerSet.installed_date</c>.</summary>
    public int Date { get; set; }

    public static string MakeId(long userId, long stickerSetId) => $"{userId}:{stickerSetId}";
}
