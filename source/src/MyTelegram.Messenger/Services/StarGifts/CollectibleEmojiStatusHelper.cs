using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class CollectibleEmojiStatusHelper
{
    public static TEmojiStatusCollectible ToEmojiStatus(
        UniqueStarGiftDocument doc,
        long? documentId = null,
        int? until = null,
        Func<long, bool>? documentExists = null)
    {
        var model = doc.Attributes.FirstOrDefault(a => a.Type == "model");
        var pattern = doc.Attributes.FirstOrDefault(a => a.Type == "pattern");
        var backdrop = doc.Attributes.FirstOrDefault(a => a.Type == "backdrop");

        var resolvedDocumentId = documentId ?? model?.DocumentId ?? doc.DocumentId;
        var patternDocumentId = pattern?.DocumentId ?? 0;
        if (patternDocumentId != 0 && documentExists != null && !documentExists(patternDocumentId))
        {
            patternDocumentId = 0;
        }

        return new TEmojiStatusCollectible
        {
            CollectibleId = doc.UniqueId,
            DocumentId = resolvedDocumentId,
            Title = $"{doc.Title} #{doc.Num}",
            Slug = doc.Slug,
            PatternDocumentId = patternDocumentId,
            CenterColor = backdrop?.CenterColor ?? 0,
            EdgeColor = backdrop?.EdgeColor ?? 0,
            PatternColor = backdrop?.PatternColor ?? 0,
            TextColor = backdrop?.TextColor ?? 0,
            Until = until,
        };
    }

    public static bool DocumentExists(IMongoDatabase mongoDatabase, long documentId)
    {
        return mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", new BsonInt64(documentId)))
            .Limit(1)
            .Any();
    }

    public static async Task<bool> DocumentExistsAsync(IMongoDatabase mongoDatabase, long documentId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("DocumentId", new BsonInt64(documentId)))
            .Limit(1)
            .AnyAsync();
    }
}
