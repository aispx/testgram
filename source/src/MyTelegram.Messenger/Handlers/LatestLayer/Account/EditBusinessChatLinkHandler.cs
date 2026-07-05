using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessChatLink = MyTelegram.Schema.TBusinessChatLink;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class EditBusinessChatLinkHandler : RpcResultObjectHandler<RequestEditBusinessChatLink, IBusinessChatLink>
{
    private readonly IMongoDatabase _database;

    public EditBusinessChatLinkHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBusinessChatLink> HandleCoreAsync(IRequestInput input, RequestEditBusinessChatLink obj)
    {
        var userId = input.UserId;
        var slug = obj.Slug;
        var linkInput = obj.Link;

        if (string.IsNullOrEmpty(slug))
        {
            RpcErrors.RpcErrors400.ChatLinkSlugEmpty.ThrowRpcError();
        }

        var collection = _database.GetCollection<BsonDocument>("businesschatlinks");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            Builders<BsonDocument>.Filter.Eq("Slug", slug)
        );

        var update = Builders<BsonDocument>.Update
            .Set("Title", linkInput.Title ?? "")
            .Set("Message", linkInput.Message ?? "");

        var result = await collection.UpdateOneAsync(filter, update);

        if (result.MatchedCount == 0)
        {
            RpcErrors.RpcErrors400.ChatLinkSlugExpired.ThrowRpcError();
        }

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        return new TBusinessChatLink
        {
            Link = TelegramDeepLinkHelper.GetBusinessChatLink(slug),
            Title = doc?.GetValue("Title", "").AsString ?? "",
            Message = doc?.GetValue("Message", "").AsString ?? "",
            Views = doc?.GetValue("Views", 0).AsInt32 ?? 0
        };
    }
}
