using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class DeleteBusinessChatLinkHandler : RpcResultObjectHandler<RequestDeleteBusinessChatLink, IBool>
{
    private readonly IMongoDatabase _database;

    public DeleteBusinessChatLinkHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDeleteBusinessChatLink obj)
    {
        var userId = input.UserId;
        var slug = obj.Slug;

        if (string.IsNullOrEmpty(slug))
        {
            RpcErrors.RpcErrors400.ChatLinkSlugEmpty.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("businesschatlinks");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("Slug", slug)
        );

        var result = await collection.DeleteOneAsync(filter);

        if (result.DeletedCount == 0)
        {
            RpcErrors.RpcErrors400.ChatLinkSlugExpired.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
