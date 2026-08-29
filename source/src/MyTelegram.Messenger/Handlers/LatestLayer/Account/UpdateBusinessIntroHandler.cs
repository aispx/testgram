using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessIntro = MyTelegram.Schema.TBusinessIntro;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdateBusinessIntroHandler : RpcResultObjectHandler<RequestUpdateBusinessIntro, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IUserAppService _userAppService;
    private readonly IFileReferenceHelper _fileReferenceHelper;

    public UpdateBusinessIntroHandler(IMongoDatabase database, IUserAppService userAppService,
        IFileReferenceHelper fileReferenceHelper)
    {
        _database = database;
        _userAppService = userAppService;
        _fileReferenceHelper = fileReferenceHelper;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessIntro obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

        if (obj.Flags.IsBitSet(0) && obj.Intro != null)
        {
            var intro = obj.Intro;
            IDocument? sticker = null;
            if (intro.Sticker is TInputDocument inputDoc)
            {
                sticker = new TDocument
                {
                    Id = inputDoc.Id,
                    AccessHash = inputDoc.AccessHash,
                    // Derived here rather than echoed back from the request: the value is stored on the
                    // user row and served to everyone who opens the profile, so it cannot be the client's
                    // to choose. See https://corefork.telegram.org/api/file-references
                    FileReference = _fileReferenceHelper.Create(AccessHashType.Document, inputDoc.Id),
                    Date = 0,
                    MimeType = string.Empty,
                    Size = 0,
                    // Never advertise dc_id=0: the client would try to fetch the sticker
                    // from a non-existent datacenter and spam help.getConfig.
                    DcId = MyTelegramConsts.MediaDcId,
                    Attributes = new TVector<IDocumentAttribute>()
                };
            }

            var businessIntro = new TBusinessIntro
            {
                Flags = intro.Flags,
                Title = intro.Title ?? string.Empty,
                Description = intro.Description ?? string.Empty,
                Sticker = sticker
            };

            var update = Builders<BsonDocument>.Update.Set("BusinessIntro", businessIntro.ToBsonDocument());
            await collection.UpdateOneAsync(filter, update);
        }
        else
        {
            var update = Builders<BsonDocument>.Update.Unset("BusinessIntro");
            await collection.UpdateOneAsync(filter, update);
        }

        _userAppService.InvalidateCache(userId);

        return new TBoolTrue();
    }
}
