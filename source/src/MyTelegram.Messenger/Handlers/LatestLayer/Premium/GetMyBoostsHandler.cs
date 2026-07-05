using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Schema.Premium;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Premium;
/// <summary>
/// Obtain which peers are we currently <a href="https://corefork.telegram.org/api/boost">boosting</a>, and how many <a href="https://corefork.telegram.org/api/boost">boost slots</a> we have left.
/// <para><c>See <a href="https://corefork.telegram.org/method/premium.getMyBoosts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMyBoostsHandler : RpcResultObjectHandler<MyTelegram.Schema.Premium.RequestGetMyBoosts, MyTelegram.Schema.Premium.IMyBoosts>
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IChatConverterService _chatConverterService;
    private readonly IQueryProcessor _queryProcessor;

    public GetMyBoostsHandler(
        IMongoDatabase mongoDatabase,
        IChatConverterService chatConverterService,
        IQueryProcessor queryProcessor)
    {
        _mongoDatabase = mongoDatabase;
        _chatConverterService = chatConverterService;
        _queryProcessor = queryProcessor;
    }

    private static long GetInt64(BsonValue v)
    {
        return v.BsonType switch
        {
            BsonType.Int64 => v.AsInt64,
            BsonType.Int32 => v.AsInt32,
            BsonType.Double => (long)v.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {v.BsonType} to Int64")
        };
    }

    protected override async Task<IMyBoosts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Premium.RequestGetMyBoosts obj)
    {
        var collection = _mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var boosts = await collection.Find(filter).ToListAsync();

        var myBoosts = new List<IMyBoost>();
        var channelIds = new HashSet<long>();

        foreach (var boost in boosts)
        {
            var channelId = GetInt64(boost["ChannelId"]);

            // Add to channelIds only if boost is active (ChannelId != 0)
            if (channelId != 0)
            {
                channelIds.Add(channelId);
            }

            myBoosts.Add(new TMyBoost
            {
                Slot = boost["Slot"].AsInt32,
                // Peer is null for free slots (ChannelId == 0) - this is correct!
                Peer = channelId != 0 ? new TPeerChannel { ChannelId = channelId } : null,
                Date = boost["Date"].AsInt32,
                Expires = boost["Expires"].AsInt32
            });
        }

        var channelIdList = channelIds.ToList();
        var channelMemberReadModels = channelIdList.Count == 0
            ? []
            : await _queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
        var chats = await _chatConverterService.GetChannelListAsync(input, channelIdList, channelMemberReadModels, input.Layer);

        return new TMyBoosts
        {
            MyBoosts = new TVector<IMyBoost>(myBoosts),
            Chats = new TVector<IChat>(chats),
            Users = new TVector<IUser>()
        };
    }
}
