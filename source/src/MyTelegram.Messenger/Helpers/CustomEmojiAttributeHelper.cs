using MongoDB.Bson;

namespace MyTelegram.Messenger.Helpers;

internal static class CustomEmojiAttributeHelper
{
    public static bool TryGetCustomEmojiAttribute(BsonDocument document, out TDocumentAttributeCustomEmoji attribute)
    {
        attribute = null!;
        if (!document.Contains("Attributes2") || document["Attributes2"].IsBsonNull || !document["Attributes2"].IsBsonArray)
        {
            return false;
        }

        foreach (var value in document["Attributes2"].AsBsonArray)
        {
            if (!value.IsBsonDocument)
            {
                continue;
            }

            var attrDoc = value.AsBsonDocument;
            if (!IsType(attrDoc, nameof(TDocumentAttributeCustomEmoji)))
            {
                continue;
            }

            attribute = new TDocumentAttributeCustomEmoji
            {
                Alt = attrDoc.TryGetValue("Alt", out var alt) && alt.IsString ? alt.AsString : string.Empty,
                Free = attrDoc.TryGetValue("Free", out var free) && free.IsBoolean && free.AsBoolean,
                TextColor = attrDoc.TryGetValue("TextColor", out var textColor) && textColor.IsBoolean && textColor.AsBoolean,
                Stickerset = ParseStickerSet(attrDoc)
            };
            return true;
        }

        return false;
    }

    public static bool TryGetStickerAttributeAsCustomEmoji(BsonDocument document, out TDocumentAttributeCustomEmoji attribute)
    {
        attribute = null!;
        if (!document.Contains("Attributes2") || document["Attributes2"].IsBsonNull || !document["Attributes2"].IsBsonArray)
        {
            return false;
        }

        foreach (var value in document["Attributes2"].AsBsonArray)
        {
            if (!value.IsBsonDocument)
            {
                continue;
            }

            var attrDoc = value.AsBsonDocument;
            if (!IsType(attrDoc, nameof(TDocumentAttributeSticker)))
            {
                continue;
            }

            attribute = new TDocumentAttributeCustomEmoji
            {
                Alt = attrDoc.TryGetValue("Alt", out var alt) && alt.IsString ? alt.AsString : "🎁",
                Free = true,
                Stickerset = ParseStickerSet(attrDoc)
            };
            return true;
        }

        return false;
    }

    private static IInputStickerSet ParseStickerSet(BsonDocument attrDoc)
    {
        if (!attrDoc.TryGetValue("Stickerset", out var stickerSetValue) || stickerSetValue.IsBsonNull || !stickerSetValue.IsBsonDocument)
        {
            return new TInputStickerSetEmpty();
        }

        var stickerSetDoc = stickerSetValue.AsBsonDocument;
        if (IsType(stickerSetDoc, nameof(TInputStickerSetID)))
        {
            return new TInputStickerSetID
            {
                Id = GetInt64(stickerSetDoc, "Id"),
                AccessHash = GetInt64(stickerSetDoc, "AccessHash")
            };
        }

        if (IsType(stickerSetDoc, nameof(TInputStickerSetShortName)))
        {
            return new TInputStickerSetShortName
            {
                ShortName = stickerSetDoc.TryGetValue("ShortName", out var shortName) && shortName.IsString
                    ? shortName.AsString
                    : string.Empty
            };
        }

        return new TInputStickerSetEmpty();
    }

    private static bool IsType(BsonDocument document, string typeName)
    {
        return document.TryGetValue("_t", out var discriminator) &&
               discriminator.IsString &&
               discriminator.AsString.EndsWith(typeName, StringComparison.Ordinal);
    }

    private static long GetInt64(BsonDocument document, string fieldName)
    {
        if (!document.TryGetValue(fieldName, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
