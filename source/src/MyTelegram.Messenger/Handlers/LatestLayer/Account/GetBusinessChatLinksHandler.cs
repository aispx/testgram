using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessChatLink = MyTelegram.Schema.TBusinessChatLink;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class GetBusinessChatLinksHandler : RpcResultObjectHandler<RequestGetBusinessChatLinks, IBusinessChatLinks>
{
    private readonly IMongoDatabase _database;

    public GetBusinessChatLinksHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBusinessChatLinks> HandleCoreAsync(IRequestInput input, RequestGetBusinessChatLinks obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("businesschatlinks");
        
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var documents = await collection.Find(filter).ToListAsync();

        var links = new TVector<IBusinessChatLink>();

        foreach (var doc in documents)
        {
            var slug = doc.GetValue("Slug", "").AsString;
            var title = doc.GetValue("Title", "").AsString;
            var message = doc.GetValue("Message", "").AsString;
            var views = doc.GetValue("Views", 0).AsInt32;

            links.Add(new TBusinessChatLink
            {
                Link = TelegramDeepLinkHelper.GetBusinessChatLink(slug),
                Title = title,
                Message = message,
                Views = views
            });
        }

        return new TBusinessChatLinks
        {
            Links = links,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}
