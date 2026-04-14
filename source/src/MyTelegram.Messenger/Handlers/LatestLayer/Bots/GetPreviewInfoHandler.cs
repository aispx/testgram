using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Get preview media for a bot
/// See https://core.telegram.org/method/bots.getPreviewInfo
/// </summary>
internal sealed class GetPreviewInfoHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<RequestGetPreviewInfo, IPreviewInfo>
{
    protected override async Task<IPreviewInfo> HandleCoreAsync(IRequestInput input, RequestGetPreviewInfo obj)
    {
        // Get bot user ID
        long botUserId;
        if (obj.Bot is TInputUser inputUser)
        {
            botUserId = inputUser.UserId;
        }
        else if (obj.Bot is TInputUserSelf)
        {
            botUserId = input.UserId;
        }
        else
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        // Verify bot exists and is a bot
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botUserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Query bot preview media from MongoDB
        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_preview_media");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("BotUserId", botUserId),
            Builders<BsonDocument>.Filter.Eq("LangCode", obj.LangCode ?? "")
        );

        var mediaDocs = await collection.Find(filter).Sort(Builders<BsonDocument>.Sort.Ascending("Date")).ToListAsync();

        var media = new TVector<IBotPreviewMedia>();
        foreach (var doc in mediaDocs)
        {
            var messageMedia = ConvertToMessageMedia(doc);
            if (messageMedia != null)
            {
                media.Add(new TBotPreviewMedia
                {
                    Date = doc["Date"].AsInt32,
                    Media = messageMedia
                });
            }
        }

        // Get all available language codes for this bot
        var allLangCodesFilter = Builders<BsonDocument>.Filter.Eq("BotUserId", botUserId);
        var langCodesDocs = await collection.Distinct<string>("LangCode", allLangCodesFilter).ToListAsync();

        return new TPreviewInfo
        {
            Media = media,
            LangCodes = new TVector<string>(langCodesDocs.Where(l => !string.IsNullOrEmpty(l)))
        };
    }

    private IMessageMedia? ConvertToMessageMedia(BsonDocument doc)
    {
        if (!doc.Contains("MediaType"))
            return null;

        var mediaType = doc["MediaType"].AsString;

        return mediaType switch
        {
            "photo" => new TMessageMediaPhoto
            {
                Photo = new TPhoto
                {
                    Id = doc["PhotoId"].AsInt64,
                    AccessHash = doc["AccessHash"].AsInt64,
                    FileReference = doc.Contains("FileReference") ? doc["FileReference"].AsByteArray : [],
                    Date = doc["Date"].AsInt32,
                    Sizes = new TVector<IPhotoSize>(),
                    DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2
                }
            },
            "document" => new TMessageMediaDocument
            {
                Document = new TDocument
                {
                    Id = doc["DocumentId"].AsInt64,
                    AccessHash = doc["AccessHash"].AsInt64,
                    FileReference = doc.Contains("FileReference") ? doc["FileReference"].AsByteArray : [],
                    Date = doc["Date"].AsInt32,
                    MimeType = doc.Contains("MimeType") ? doc["MimeType"].AsString : "video/mp4",
                    Size = doc.Contains("Size") ? doc["Size"].AsInt64 : 0,
                    Thumbs = new TVector<IPhotoSize>(),
                    VideoThumbs = new TVector<IVideoSize>(),
                    DcId = doc.Contains("DcId") ? doc["DcId"].AsInt32 : 2,
                    Attributes = new TVector<IDocumentAttribute>()
                }
            },
            _ => null
        };
    }
}
