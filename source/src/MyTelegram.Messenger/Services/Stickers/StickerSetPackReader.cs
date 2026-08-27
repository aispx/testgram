using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Reads the pack, keyword, alt and thumbnail structures out of a sticker catalogue row.
/// </summary>
public static class StickerSetPackReader
{
    /// <summary>
    /// Seeded rows keep the short name in <c>Slug</c>, rows created here in <c>ShortName</c>. Callers
    /// must not assume either exists on its own — indexing the missing one used to throw.
    /// </summary>
    public static string ReadShortName(BsonDocument stickerSetDocument)
    {
        var shortName = stickerSetDocument.GetString("ShortName");

        return shortName.Length > 0 ? shortName : stickerSetDocument.GetString("Slug");
    }

    /// <summary>
    /// <c>stickerPack</c> vector as stored. Packs are the emoji index: one entry per emoji, listing every
    /// document in the set that carries it.
    /// </summary>
    public static List<IStickerPack> ReadPacks(BsonDocument stickerSetDocument, string? diceEmoticon = null)
    {
        var packs = new List<IStickerPack>();

        if (stickerSetDocument.TryGetValue("Packs", out var packsValue) && packsValue.IsBsonArray)
        {
            foreach (var value in packsValue.AsBsonArray.Where(p => p.IsBsonDocument))
            {
                var pack = value.AsBsonDocument;
                packs.Add(new TStickerPack
                {
                    Emoticon = pack.GetString("Emoticon"),
                    Documents = new TVector<long>(pack.GetInt64List("Documents"))
                });
            }
        }

        // A dice set has no packs of its own: every animation belongs to the one emoji that was asked for.
        if (packs.Count == 0 && !string.IsNullOrEmpty(diceEmoticon))
        {
            packs.Add(new TStickerPack
            {
                Emoticon = diceEmoticon,
                Documents = new TVector<long>(stickerSetDocument.GetInt64List("DocumentIds"))
            });
        }

        return packs;
    }

    public static List<IStickerKeyword> ReadKeywords(BsonDocument stickerSetDocument)
    {
        if (!stickerSetDocument.TryGetValue("Keywords", out var value) || !value.IsBsonArray)
        {
            return [];
        }

        var keywords = new List<IStickerKeyword>();
        foreach (var item in value.AsBsonArray.Where(p => p.IsBsonDocument))
        {
            var keyword = item.AsBsonDocument;
            if (!keyword.TryGetValue("Keyword", out var words) || !words.IsBsonArray)
            {
                continue;
            }

            keywords.Add(new TStickerKeyword
            {
                DocumentId = keyword.GetInt64("DocumentId"),
                Keyword = new TVector<string>(words.AsBsonArray.Where(p => p.IsString).Select(p => p.AsString))
            });
        }

        return keywords;
    }

    /// <summary>
    /// Which emoji each document belongs to, from the packs. Used both as the <c>alt</c> fallback and as
    /// the alt that goes into <see cref="StickerSetHashHelper"/>.
    /// </summary>
    public static Dictionary<long, string> BuildAltByDocumentId(BsonDocument stickerSetDocument,
        string? fallbackEmoticon = null)
    {
        var result = new Dictionary<long, string>();

        if (stickerSetDocument.TryGetValue("Packs", out var packsValue) && packsValue.IsBsonArray)
        {
            foreach (var value in packsValue.AsBsonArray.Where(p => p.IsBsonDocument))
            {
                var pack = value.AsBsonDocument;
                var emoticon = pack.GetString("Emoticon");

                foreach (var documentId in pack.GetInt64List("Documents"))
                {
                    result.TryAdd(documentId, emoticon);
                }
            }
        }

        if (!string.IsNullOrEmpty(fallbackEmoticon))
        {
            foreach (var documentId in stickerSetDocument.GetInt64List("DocumentIds"))
            {
                result.TryAdd(documentId, fallbackEmoticon);
            }
        }

        return result;
    }

    /// <summary>
    /// The stored <c>PhotoSize</c> vector of a document or of a set thumbnail. Preserves the concrete
    /// constructor, because clients pick a preview by its <c>type</c> and treat the stripped and path
    /// variants as inline data rather than something to download.
    /// See https://corefork.telegram.org/api/files#image-thumbnail-types
    /// </summary>
    public static TVector<IPhotoSize> ReadThumbs(BsonDocument document, string name = "Thumbs")
    {
        var result = new TVector<IPhotoSize>();
        if (!document.TryGetValue(name, out var thumbsValue) || !thumbsValue.IsBsonArray)
        {
            return result;
        }

        foreach (var value in thumbsValue.AsBsonArray.Where(p => p.IsBsonDocument))
        {
            var thumb = value.AsBsonDocument;
            var thumbType = thumb.GetString("Type");

            switch (thumb.GetString("_t"))
            {
                case nameof(TPhotoSize):
                    result.Add(new TPhotoSize
                    {
                        Type = thumbType,
                        W = thumb.GetInt32("W"),
                        H = thumb.GetInt32("H"),
                        Size = thumb.GetInt32("Size")
                    });
                    break;
                case nameof(TPhotoCachedSize):
                    result.Add(new TPhotoCachedSize
                    {
                        Type = thumbType,
                        W = thumb.GetInt32("W"),
                        H = thumb.GetInt32("H"),
                        Bytes = thumb.GetFileReference("Bytes")
                    });
                    break;
                case nameof(TPhotoSizeProgressive):
                    result.Add(new TPhotoSizeProgressive
                    {
                        Type = thumbType,
                        W = thumb.GetInt32("W"),
                        H = thumb.GetInt32("H"),
                        Sizes = new TVector<int>(thumb.GetInt64List("Sizes").Select(p => (int)p))
                    });
                    break;
                case nameof(TPhotoStrippedSize):
                    result.Add(new TPhotoStrippedSize { Type = thumbType, Bytes = thumb.GetFileReference("Bytes") });
                    break;
                case nameof(TPhotoPathSize):
                    result.Add(new TPhotoPathSize { Type = thumbType, Bytes = thumb.GetFileReference("Bytes") });
                    break;
                case nameof(TPhotoSizeEmpty):
                    result.Add(new TPhotoSizeEmpty { Type = thumbType });
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Stored <c>VideoSize</c> vector. A sticker's animated preview lives here, and for Premium stickers
    /// so does the effect: clients recognise one by a video thumbnail of type <c>"f"</c>
    /// (Android <c>MessageObject.isPremiumSticker</c>), so dropping these turns every Premium sticker into
    /// an ordinary one.
    /// </summary>
    public static TVector<IVideoSize> ReadVideoThumbs(BsonDocument document, string name = "VideoThumbs")
    {
        var result = new TVector<IVideoSize>();
        if (!document.TryGetValue(name, out var value) || !value.IsBsonArray)
        {
            return result;
        }

        foreach (var item in value.AsBsonArray.Where(p => p.IsBsonDocument))
        {
            var videoSize = item.AsBsonDocument;
            var type = videoSize.GetString("Type");
            if (type.Length == 0)
            {
                continue;
            }

            result.Add(new TVideoSize
            {
                Type = type,
                W = videoSize.GetInt32("W"),
                H = videoSize.GetInt32("H"),
                Size = videoSize.GetInt32("Size"),
                VideoStartTs = videoSize.TryGetValue("VideoStartTs", out var startTs) && startTs.IsDouble
                    ? startTs.AsDouble
                    : null
            });
        }

        return result;
    }
}
