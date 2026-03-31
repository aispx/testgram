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

    public UpdateBusinessIntroHandler(IMongoDatabase database, IUserAppService userAppService)
    {
        _database = database;
        _userAppService = userAppService;
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
                    FileReference = inputDoc.FileReference
                };
            }

            var businessIntro = new TBusinessIntro
            {
                Flags = intro.Flags,
                Title = intro.Title,
                Description = intro.Description,
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
