using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class GetChatsToSendHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService)
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
            return new TChats { Chats = new TVector<IChat>() };
        }
        
        var channelMemberReadModels = await queryProcessor.ProcessAsync(
            new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
        var chats = await chatConverterService.GetChannelListAsync(
            input,
            channelIds,
            channelMemberReadModels,
            input.Layer);
        
        return new TChats { Chats = new TVector<IChat>(chats) };
    }
}
