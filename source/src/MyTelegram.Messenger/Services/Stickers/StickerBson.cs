using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Reads values out of the raw sticker catalogue documents.
///
/// <para>The catalogue (<c>eventflow-stickersetreadmodel</c>) and the document read model are written
/// by several producers — the seeder scripts, the closed-source file-server and this repo — so the
/// same field arrives as <c>Int32</c>, <c>Int64</c> or <c>Double</c> depending on who wrote it, and
/// <c>FileReference</c> as either <c>Binary</c> or an array of numbers. Every sticker handler used to
/// carry its own private copy of these converters; this is that copy, once.</para>
/// </summary>
public static class StickerBson
{
    public static long ToInt64(BsonValue? value, long defaultValue = 0)
    {
        return value?.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            BsonType.String when long.TryParse(value.AsString, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static int ToInt32(BsonValue? value, int defaultValue = 0)
    {
        return value?.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => (int)value.AsInt64,
            BsonType.Double => (int)value.AsDouble,
            BsonType.String when int.TryParse(value.AsString, out var parsed) => parsed,
            _ => defaultValue
        };
    }

    public static long GetInt64(this BsonDocument document, string name, long defaultValue = 0)
    {
        return document.TryGetValue(name, out var value) ? ToInt64(value, defaultValue) : defaultValue;
    }

    public static int GetInt32(this BsonDocument document, string name, int defaultValue = 0)
    {
        return document.TryGetValue(name, out var value) ? ToInt32(value, defaultValue) : defaultValue;
    }

    public static bool GetBool(this BsonDocument document, string name)
    {
        return document.TryGetValue(name, out var value) && !value.IsBsonNull && value.ToBoolean();
    }

    public static string GetString(this BsonDocument document, string name, string defaultValue = "")
    {
        return document.TryGetValue(name, out var value) && value.IsString ? value.AsString : defaultValue;
    }

    public static List<long> GetInt64List(this BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value) || !value.IsBsonArray)
        {
            return [];
        }

        return value.AsBsonArray.Select(p => ToInt64(p)).ToList();
    }

    /// <summary>
    /// A <c>file_reference</c> may be absent, <c>Binary</c> or an array of byte-sized numbers.
    /// Returning an empty array for the absent case is intentional: the field is required on the wire
    /// and an empty reference simply means "not tied to a message".
    /// </summary>
    public static byte[] GetFileReference(this BsonDocument document, string name = "FileReference")
    {
        if (!document.TryGetValue(name, out var value) || value.IsBsonNull)
        {
            return [];
        }

        return value.BsonType switch
        {
            BsonType.Binary => value.AsBsonBinaryData.Bytes,
            BsonType.Array => value.AsBsonArray.Select(p => (byte)ToInt32(p)).ToArray(),
            _ => []
        };
    }
}
