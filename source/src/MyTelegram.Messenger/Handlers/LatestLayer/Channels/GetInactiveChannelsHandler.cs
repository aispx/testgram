using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Get inactive channels and supergroups
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.getInactiveChannels"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetInactiveChannelsHandler(
    IMongoDatabase mongoDatabase,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestGetInactiveChannels, MyTelegram.Schema.Messages.IInactiveChats>
{
    protected override async Task<MyTelegram.Schema.Messages.IInactiveChats> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestGetInactiveChannels obj)
    {
        var memberCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
        var messageCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-messagereadmodel");

        var memberFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("Left", false),
            Builders<BsonDocument>.Filter.Eq("Kicked", false)
        );
        var members = await memberCol.Find(memberFilter).ToListAsync();

        var inactiveChannels = new List<(long channelId, int lastMessageDate)>();
        var currentTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inactiveThreshold = currentTime - (90 * 24 * 60 * 60); // 90 days

        foreach (var member in members)
        {
            var channelId = member["ChannelId"].AsInt64;

            var msgFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("SenderUserId", input.UserId),
                Builders<BsonDocument>.Filter.Eq("OwnerPeerId", channelId)
            );
            var lastMessage = await messageCol.Find(msgFilter)
                .SortByDescending(m => m["Date"])
                .Limit(1)
                .FirstOrDefaultAsync();

            int lastMessageDate = 0;
            if (lastMessage != null && lastMessage.Contains("Date"))
            {
                lastMessageDate = lastMessage["Date"].AsInt32;
            }
            else
            {
                var joinDate = member.Contains("JoinDate") ? member["JoinDate"].AsInt32 : 0;
                lastMessageDate = joinDate;
            }

            if (lastMessageDate < inactiveThreshold)
            {
                inactiveChannels.Add((channelId, lastMessageDate));
            }
        }

        if (inactiveChannels.Count == 0)
        {
            return new TInactiveChats { Chats = new TVector<IChat>(), Dates = new TVector<int>(), Users = new TVector<IUser>() };
        }

        inactiveChannels = inactiveChannels.OrderBy(x => x.lastMessageDate).Take(10).ToList();
        var channelIds = inactiveChannels.Select(x => x.channelId).ToList();
        var dates = inactiveChannels.Select(x => x.lastMessageDate).ToList();

        var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIds));
        var channels = await chatConverterService.GetChannelListAsync(input, channelIds, channelMemberReadModels, input.Layer);

        return new TInactiveChats
        {
            Chats = new TVector<IChat>(channels),
            Dates = new TVector<int>(dates),
            Users = new TVector<IUser>()
        };
    }
}