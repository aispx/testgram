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

    public GetMyBoostsHandler(IMongoDatabase mongoDatabase)
    {
        _mongoDatabase = mongoDatabase;
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
            var channelId = boost["ChannelId"].AsInt64;
            channelIds.Add(channelId);

            myBoosts.Add(new TMyBoost
            {
                Slot = boost["Slot"].AsInt32,
                Peer = new TPeerChannel { ChannelId = channelId },
                Date = boost["Date"].AsInt32,
                Expires = boost["Expires"].AsInt32
            });
        }

        var channelCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.In("ChannelId", channelIds);
        var channels = await channelCol.Find(channelFilter).ToListAsync();

        var chats = new List<IChat>();
        foreach (var channel in channels)
        {
            var broadcast = channel.Contains("Broadcast") && channel["Broadcast"].AsBoolean;
            var megagroup = channel.Contains("MegaGroup") && channel["MegaGroup"].AsBoolean;

            // Fix for old channels without Megagroup flag: if not broadcast, it's a megagroup
            if (!broadcast && !megagroup)
            {
                megagroup = true;
            }

            chats.Add(new TChannel
            {
                Id = channel["ChannelId"].AsInt64,
                AccessHash = channel["AccessHash"].AsInt64,
                Title = channel["Title"].AsString,
                Username = channel.Contains("UserName") ? channel["UserName"].AsString : null,
                Photo = new TChatPhotoEmpty(),
                Date = channel.Contains("Date") ? channel["Date"].AsInt32 : 0,
                RestrictionReason = [],
                Broadcast = broadcast,
                Megagroup = megagroup
            });
        }

        return new TMyBoosts
        {
            MyBoosts = new TVector<IMyBoost>(myBoosts),
            Chats = new TVector<IChat>(chats),
            Users = []
        };
    }
}