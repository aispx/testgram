namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

using MongoDB.Bson;
using MongoDB.Driver;

/// <summary>
/// Get available message reactions
/// See https://core.telegram.org/method/messages.getAvailableReactions
/// </summary>
internal sealed class GetAvailableReactionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAvailableReactions, MyTelegram.Schema.Messages.IAvailableReactions>
{
    private readonly IMongoDatabase _database;

    public GetAvailableReactionsHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<MyTelegram.Schema.Messages.IAvailableReactions> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetAvailableReactions obj)
    {
        // Load reactions from MongoDB
        var collection = _database.GetCollection<BsonDocument>("reactions");
        var filter = Builders<BsonDocument>.Filter.Empty;
        var sort = Builders<BsonDocument>.Sort.Ascending("Order");
        var reactionDocs = await collection.Find(filter).Sort(sort).ToListAsync();

        if (reactionDocs.Count == 0)
        {
            // Return empty if no reactions in database
            return new TAvailableReactions
            {
                Reactions = new TVector<IAvailableReaction>(),
                Hash = 0
            };
        }

        // Convert MongoDB documents to TAvailableReaction objects
        var reactions = new List<IAvailableReaction>();
        foreach (var doc in reactionDocs)
        {
            var reaction = new TAvailableReaction
            {
                Reaction = doc["Reaction"].AsString,
                Title = doc["Title"].AsString,
                Inactive = doc.Contains("Inactive") && doc["Inactive"].AsBoolean,
                Premium = doc.Contains("Premium") && doc["Premium"].AsBoolean,
                StaticIcon = ConvertToDocument(doc["StaticIcon"].AsBsonDocument),
                AppearAnimation = ConvertToDocument(doc["AppearAnimation"].AsBsonDocument),
                SelectAnimation = ConvertToDocument(doc["SelectAnimation"].AsBsonDocument),
                ActivateAnimation = ConvertToDocument(doc["ActivateAnimation"].AsBsonDocument),
                EffectAnimation = ConvertToDocument(doc["EffectAnimation"].AsBsonDocument),
                AroundAnimation = doc.Contains("AroundAnimation") && !doc["AroundAnimation"].IsBsonNull
                    ? ConvertToDocument(doc["AroundAnimation"].AsBsonDocument)
                    : new TDocumentEmpty(),
                CenterIcon = doc.Contains("CenterIcon") && !doc["CenterIcon"].IsBsonNull
                    ? ConvertToDocument(doc["CenterIcon"].AsBsonDocument)
                    : new TDocumentEmpty()
            };
            reactions.Add(reaction);
        }

        // Calculate hash
        var hash = reactions.Aggregate(0, (h, r) => h * 31 + ((TAvailableReaction)r).Reaction.GetHashCode()) & int.MaxValue;

        // Check if client has same hash
        if (obj.Hash != 0 && obj.Hash == hash)
            return new TAvailableReactionsNotModified();

        return new TAvailableReactions
        {
            Reactions = new TVector<IAvailableReaction>(reactions),
            Hash = hash
        };
    }

    private static TDocument ConvertToDocument(BsonDocument doc)
    {
        return new TDocument
        {
            Id = doc["Id"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            FileReference = doc["FileReference"].AsByteArray,
            Date = doc["Date"].AsInt32,
            MimeType = doc["MimeType"].AsString,
            Size = doc["Size"].AsInt64,
            Thumbs = new TVector<IPhotoSize>(),
            VideoThumbs = new TVector<IVideoSize>(),
            DcId = doc["DcId"].AsInt32,
            Attributes = new TVector<IDocumentAttribute>()
        };
    }
}
