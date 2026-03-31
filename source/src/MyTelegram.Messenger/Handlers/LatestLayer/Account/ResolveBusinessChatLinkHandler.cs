using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessChatLink = MyTelegram.Schema.TBusinessChatLink;
using TPeerUser = MyTelegram.Schema.TPeerUser;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class ResolveBusinessChatLinkHandler : RpcResultObjectHandler<RequestResolveBusinessChatLink, IResolvedBusinessChatLinks>
{
    private readonly IMongoDatabase _database;

    public ResolveBusinessChatLinkHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IResolvedBusinessChatLinks> HandleCoreAsync(IRequestInput input, RequestResolveBusinessChatLink obj)
    {
        var slug = obj.Slug;

        if (string.IsNullOrEmpty(slug))
        {
            RpcErrors.RpcErrors400.ChatLinkSlugEmpty.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("businesschatlinks");
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", slug);
        
        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        if (doc == null)
        {
            RpcErrors.RpcErrors400.ChatLinkSlugExpired.ThrowRpcError();
        }

        var incUpdate = Builders<BsonDocument>.Update.Inc("Views", 1);
        await collection.UpdateOneAsync(filter, incUpdate);

        var userId = doc.GetValue("UserId", 0).AsInt64;
        var message = doc.GetValue("Message", "").AsString;

        return new TResolvedBusinessChatLinks
        {
            Peer = new TPeerUser { UserId = userId },
            Message = message,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}
