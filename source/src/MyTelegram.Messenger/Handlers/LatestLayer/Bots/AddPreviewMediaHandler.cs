using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Add a <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">main mini app preview, see here »</a> for more info.Only owners of bots with a configured Main Mini App can use this method, see <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-app-previews">see here »</a> for more info on how to check if you can invoke this method.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.addPreviewMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AddPreviewMediaHandler(
    IMongoDatabase mongoDatabase,
    IFileReferenceHelper fileReferenceHelper,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestAddPreviewMedia, MyTelegram.Schema.IBotPreviewMedia>
{
    protected override async Task<MyTelegram.Schema.IBotPreviewMedia> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestAddPreviewMedia obj)
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
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        // Verify bot exists and is a bot
        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botUserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_preview_media");
        var date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Build document based on media type
        var doc = new BsonDocument
        {
            ["_id"] = $"bot-preview-media-{botUserId}-{date}-{Guid.NewGuid()}",
            ["BotUserId"] = botUserId,
            ["LangCode"] = obj.LangCode ?? "",
            ["Date"] = date
        };

        IMessageMedia? resultMedia = null;

        if (obj.Media is TInputMediaPhoto photoMedia && photoMedia.Id is TInputPhoto inputPhoto)
        {
            // Existing photo
            doc["MediaType"] = "photo";
            doc["PhotoId"] = inputPhoto.Id;
            doc["AccessHash"] = inputPhoto.AccessHash;
            // The reference the client sent is not stored. It used to be written here as the canonical
            // value of the row, which let a client decide what this server would serve back to everyone.
            // References are derived from the media id instead.
            // See https://corefork.telegram.org/api/file-references

            resultMedia = new TMessageMediaPhoto
            {
                Photo = new TPhoto
                {
                    Id = inputPhoto.Id,
                    AccessHash = inputPhoto.AccessHash,
                    FileReference = fileReferenceHelper.Create(AccessHashType.Photo, inputPhoto.Id),
                    Date = date,
                    Sizes = new TVector<IPhotoSize>(),
                    DcId = 2
                }
            };
        }
        else if (obj.Media is TInputMediaDocument docMedia && docMedia.Id is TInputDocument inputDoc)
        {
            // Existing document
            doc["MediaType"] = "document";
            doc["DocumentId"] = inputDoc.Id;
            doc["AccessHash"] = inputDoc.AccessHash;
            // See the photo branch above: the client's reference is not the row's to keep.

            resultMedia = new TMessageMediaDocument
            {
                Document = new TDocument
                {
                    Id = inputDoc.Id,
                    AccessHash = inputDoc.AccessHash,
                    FileReference = fileReferenceHelper.Create(AccessHashType.Document, inputDoc.Id),
                    Date = date,
                    MimeType = "video/mp4",
                    Size = 0,
                    Thumbs = new TVector<IPhotoSize>(),
                    VideoThumbs = new TVector<IVideoSize>(),
                    DcId = 2,
                    Attributes = new TVector<IDocumentAttribute>()
                }
            };
        }
        else
        {
            // Unsupported media type - return empty
            resultMedia = new TMessageMediaEmpty();
        }

        await collection.InsertOneAsync(doc);

        return new TBotPreviewMedia
        {
            Date = date,
            Media = resultMedia ?? new TMessageMediaEmpty()
        };
    }
}