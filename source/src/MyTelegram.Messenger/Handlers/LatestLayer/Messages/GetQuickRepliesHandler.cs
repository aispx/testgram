using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;
using TQuickReply = MyTelegram.Schema.TQuickReply;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetQuickRepliesHandler : RpcResultObjectHandler<RequestGetQuickReplies, IQuickReplies>
{
    private readonly IMongoDatabase _database;

    public GetQuickRepliesHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IQuickReplies> HandleCoreAsync(IRequestInput input, RequestGetQuickReplies obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("quickreplys");
        
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var documents = await collection.Find(filter).ToListAsync();

        var quickReplies = new TVector<IQuickReply>();

        foreach (var doc in documents)
        {
            var shortcutId = doc.GetValue("ShortcutId", 0).AsInt32;
            var title = doc.GetValue("Title", "").AsString;
            var topMessage = doc.GetValue("TopMessage", 0).AsInt32;
            var count = doc.GetValue("Count", 0).AsInt32;

            quickReplies.Add(new TQuickReply
            {
                ShortcutId = shortcutId,
                Shortcut = title,
                TopMessage = topMessage,
                Count = count
            });
        }

        return new TQuickReplies
        {
            QuickReplies = quickReplies,
            Messages = new TVector<IMessage>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        };
    }
}
