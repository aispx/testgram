using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessChatLink = MyTelegram.Schema.TBusinessChatLink;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class CreateBusinessChatLinkHandler : RpcResultObjectHandler<RequestCreateBusinessChatLink, IBusinessChatLink>
{
    private readonly IMongoDatabase _database;
    private const int MaxLinks = 10;

    public CreateBusinessChatLinkHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBusinessChatLink> HandleCoreAsync(IRequestInput input, RequestCreateBusinessChatLink obj)
    {
        var userId = input.UserId;
        var linkInput = obj.Link;

        var collection = _database.GetCollection<BsonDocument>("businesschatlinks");
        
        var count = await collection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId));
        if (count >= MaxLinks)
        {
            RpcErrors.RpcErrors400.ChatLinksTooMuch.ThrowRpcError();
        }

        var slug = GenerateSlug();
        var title = linkInput.Title ?? "";
        var message = linkInput.Message ?? "";
        var views = 0;

        var doc = new BsonDocument
        {
            { "UserId", userId },
            { "Slug", slug },
            { "Title", title },
            { "Message", message },
            { "Views", views },
            { "CreatedAt", DateTime.UtcNow }
        };

        await collection.InsertOneAsync(doc);

        return new TBusinessChatLink
        {
            Link = TelegramDeepLinkHelper.GetBusinessChatLink(slug),
            Title = title,
            Message = message,
            Views = views
        };
    }

    private string GenerateSlug()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, 8).Select(s => s[random.Next(s.Length)]).ToArray());
    }
}
