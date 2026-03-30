using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetChatsToSendHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetChatsToSend, IChats>
{
    private readonly IMongoCollection<BsonDocument> _channelMembersCollection =
        mongoDatabase.GetCollection<BsonDocument>("channel_members");

    protected override async Task<IChats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stories.RequestGetChatsToSend obj)
    {
        var memberFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("userId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("isMember", true)
        );
        
        var members = await _channelMembersCollection.Find(memberFilter).ToListAsync();
        var channelIds = members.Select(m => m["channelId"].AsInt64).Distinct().ToList();
        
        if (channelIds.Count == 0)
        {
            return new TChats { Chats = [] };
        }
        
        var chats = new TVector<IChat>();
        foreach (var channelId in channelIds)
        {
            chats.Add(new TChannel
            {
                Id = channelId,
                Title = "Channel",
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                AccessHash = 0
            });
        }
        
        return new TChats { Chats = chats };
    }
}