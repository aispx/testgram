using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Decides whether a document may be used as a sticker.
///
/// <para>Needed because the flat lists are addressed by <c>InputDocument</c>, and until now they accepted
/// any document at all: a photo could be favourited, and would then be handed back in
/// <c>messages.favedStickers</c>, where clients drop it — leaving their list shorter than ours and the
/// hash mismatched on every poll.</para>
/// </summary>
public interface IStickerDocumentValidator
{
    /// <summary>
    /// Whether the document exists and is a sticker. Custom emoji are a separate kind and are rejected
    /// unless <paramref name="allowCustomEmoji"/> is set — <c>faveSticker</c> and
    /// <c>saveRecentSticker</c> are documented for normal and mask stickers only.
    /// </summary>
    Task<bool> IsStickerAsync(long documentId, bool allowCustomEmoji = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the mime type is one a client can render as a sticker:
    /// <c>image/webp</c> (static), <c>application/x-tgsticker</c> (Lottie) or <c>video/webm</c> (VP9).
    /// See https://corefork.telegram.org/api/stickers#displaying-stickers
    /// </summary>
    bool IsStickerMimeType(string? mimeType);
}

/// <inheritdoc />
public class StickerDocumentValidator(IMongoDatabase mongoDatabase)
    : IStickerDocumentValidator, ITransientDependency
{
    private static readonly HashSet<string> StickerMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/webp",
        "application/x-tgsticker",
        "video/webm"
    };

    public async Task<bool> IsStickerAsync(long documentId, bool allowCustomEmoji = false,
        CancellationToken cancellationToken = default)
    {
        var row = await mongoDatabase
            .GetCollection<BsonDocument>(StickerSetMapper.DocumentCollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", documentId))
            .FirstOrDefaultAsync(cancellationToken);

        if (row == null)
        {
            return false;
        }

        // The attribute is the authority — a sticker is defined by carrying documentAttributeSticker, and
        // the mime check only guards against a row whose attributes were written optimistically.
        var attributes = row.TryGetValue("Attributes2", out var value) && !value.IsBsonNull
            ? value.ToJson()
            : string.Empty;

        var isSticker = attributes.Contains(nameof(TDocumentAttributeSticker), StringComparison.Ordinal);
        var isCustomEmoji = attributes.Contains(nameof(TDocumentAttributeCustomEmoji), StringComparison.Ordinal);

        if (!isSticker && !isCustomEmoji)
        {
            return false;
        }

        if (isCustomEmoji && !isSticker && !allowCustomEmoji)
        {
            return false;
        }

        return IsStickerMimeType(row.GetString("MimeType"));
    }

    public bool IsStickerMimeType(string? mimeType)
    {
        return mimeType != null && StickerMimeTypes.Contains(mimeType);
    }
}
