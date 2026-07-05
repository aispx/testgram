using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;

namespace MyTelegram.Messenger.Services;

internal static class FactCheckHelper
{
    private const string CollectionName = "message_factchecks";
    private const string EditorsCollectionName = "factcheckers";

    private static string DocId(long ownerPeerId, int messageId) => $"{ownerPeerId}:{messageId}";

    public static string Key(long ownerPeerId, int messageId) => DocId(ownerPeerId, messageId);

    public static async Task<bool> CanEditFactCheckAsync(IMongoDatabase database, long userId)
    {
        var builder = Builders<BsonDocument>.Filter;
        var doc = await database.GetCollection<BsonDocument>(EditorsCollectionName)
            .Find(builder.Or(
                builder.Eq("_id", userId),
                builder.Eq("_id", userId.ToString()),
                builder.Eq("UserId", userId)))
            .FirstOrDefaultAsync();
        if (doc == null)
        {
            return false;
        }

        return IsFlagEnabled(doc, "Enabled") && IsFlagEnabled(doc, "CanEditFactcheck");
    }

    public static async Task<BsonDocument?> FindAsync(IMongoDatabase database, long ownerPeerId, int messageId)
    {
        return await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", DocId(ownerPeerId, messageId)))
            .FirstOrDefaultAsync();
    }

    public static BsonDocument? Find(IMongoDatabase database, long ownerPeerId, int messageId)
    {
        return database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", DocId(ownerPeerId, messageId)))
            .FirstOrDefault();
    }

    public static IReadOnlyCollection<BsonDocument> FindMany(
        IMongoDatabase database,
        IReadOnlyCollection<(long OwnerPeerId, int MessageId)> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var builder = Builders<BsonDocument>.Filter;
        var filters = keys
            .GroupBy(p => p.OwnerPeerId)
            .Select(p => builder.And(
                builder.Eq("OwnerPeerId", p.Key),
                builder.In("MessageId", p.Select(k => k.MessageId).Distinct())))
            .ToList();

        return database.GetCollection<BsonDocument>(CollectionName)
            .Find(filters.Count == 1 ? filters[0] : builder.Or(filters))
            .ToList();
    }

    public static async Task<IReadOnlyCollection<BsonDocument>> FindManyAsync(
        IMongoDatabase database,
        long ownerPeerId,
        IReadOnlyCollection<int> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        return await database.GetCollection<BsonDocument>(CollectionName)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", ownerPeerId),
                Builders<BsonDocument>.Filter.In("MessageId", messageIds)))
            .ToListAsync();
    }

    public static async Task<BsonDocument> UpsertAsync(
        IMongoDatabase database,
        long ownerPeerId,
        int messageId,
        ITextWithEntities text,
        long editorUserId,
        int date)
    {
        var textData = SerializeText(text);
        var plainText = ExtractPlainText(text);
        var hash = ComputeHash(ownerPeerId, messageId, textData);
        var doc = new BsonDocument
        {
            ["_id"] = DocId(ownerPeerId, messageId),
            ["OwnerPeerId"] = ownerPeerId,
            ["MessageId"] = messageId,
            ["Text"] = plainText,
            ["TextData"] = textData,
            ["Hash"] = hash,
            ["EditorUserId"] = editorUserId,
            ["Date"] = date,
        };

        await database.GetCollection<BsonDocument>(CollectionName).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DocId(ownerPeerId, messageId)),
            doc,
            new ReplaceOptions { IsUpsert = true });
        return doc;
    }

    public static async Task DeleteAsync(IMongoDatabase database, long ownerPeerId, int messageId)
    {
        await database.GetCollection<BsonDocument>(CollectionName)
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DocId(ownerPeerId, messageId)));
    }

    public static TFactCheck ToFactCheck(BsonDocument doc, bool needCheck)
    {
        return needCheck
            ? new TFactCheck
            {
                NeedCheck = true,
                Hash = doc.GetValue("Hash", 0L).ToInt64(),
            }
            : new TFactCheck
            {
                Country = doc.GetValue("Country", string.Empty).AsString,
                Text = DeserializeText(doc),
                Hash = doc.GetValue("Hash", 0L).ToInt64(),
            };
    }

    public static string ExtractPlainText(ITextWithEntities text) =>
        text is TTextWithEntities textWithEntities ? textWithEntities.Text : string.Empty;

    private static byte[] SerializeText(ITextWithEntities text)
    {
        if (text is TTextWithEntities textWithEntities)
        {
            textWithEntities.Entities ??= [];
        }

        var writer = new ArrayBufferWriter<byte>();
        text.Serialize(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static ITextWithEntities DeserializeText(BsonDocument doc)
    {
        if (doc.TryGetValue("TextData", out var textData) && textData.IsBsonBinaryData)
        {
            ReadOnlyMemory<byte> buffer = textData.AsByteArray;
            return buffer.Read<ITextWithEntities>();
        }

        return new TTextWithEntities
        {
            Text = doc.GetValue("Text", string.Empty).AsString,
            Entities = [],
        };
    }

    private static long ComputeHash(long ownerPeerId, int messageId, byte[] textData)
    {
        var hashInput = new byte[sizeof(long) + sizeof(int) + textData.Length];
        BitConverter.GetBytes(ownerPeerId).CopyTo(hashInput, 0);
        BitConverter.GetBytes(messageId).CopyTo(hashInput, sizeof(long));
        textData.CopyTo(hashInput, sizeof(long) + sizeof(int));
        var hash = SHA256.HashData(hashInput);
        return BitConverter.ToInt64(hash, 0);
    }

    private static bool IsFlagEnabled(BsonDocument doc, string fieldName)
    {
        if (!doc.TryGetValue(fieldName, out var value))
        {
            return true;
        }

        return !value.IsBoolean || value.AsBoolean;
    }
}
