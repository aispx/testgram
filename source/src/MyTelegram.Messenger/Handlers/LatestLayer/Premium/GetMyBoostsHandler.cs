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

        var channelCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelFilter = Builders<BsonDocument>.Filter.In("ChannelId", channelIds);
        var channels = await channelCol.Find(channelFilter).ToListAsync();

        var chats = new List<IChat>();
        foreach (var channel in channels)
        {
            var channelId = channel["ChannelId"].AsInt64;

            // Get AdminRights from channelmemberreadmodel
            var memberCol = _mongoDatabase.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel");
            var member = await memberCol.Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("ChannelId", channelId),
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)
            )).FirstOrDefaultAsync();

            TChatAdminRights? adminRights = null;
            if (member != null && member.Contains("AdminRights") && member["AdminRights"].AsInt32 != 0)
            {
                var rights = new ChatAdminRights(member["AdminRights"].AsInt32);
                adminRights = rights.ToChatAdminRights() as TChatAdminRights;
            }

            var broadcast = channel.Contains("Broadcast") && channel["Broadcast"].AsBoolean;
            var megagroup = channel.Contains("MegaGroup") && channel["MegaGroup"].AsBoolean;

            // Fix for old channels without Megagroup flag: if not broadcast, it's a megagroup
            if (!broadcast && !megagroup)
            {
                megagroup = true;
            }

            // Read DefaultBannedRights from MongoDB
            TChatBannedRights? defaultBannedRights = null;
            if (channel.Contains("DefaultBannedRights") && !channel["DefaultBannedRights"].IsBsonNull)
            {
                var rights = channel["DefaultBannedRights"].AsBsonDocument;
                defaultBannedRights = new TChatBannedRights
                {
                    ViewMessages = rights.GetValue("ViewMessages", false).AsBoolean,
                    SendMessages = rights.GetValue("SendMessages", false).AsBoolean,
                    SendMedia = rights.GetValue("SendMedia", false).AsBoolean,
                    SendStickers = rights.GetValue("SendStickers", false).AsBoolean,
                    SendGifs = rights.GetValue("SendGifs", false).AsBoolean,
                    SendGames = rights.GetValue("SendGames", false).AsBoolean,
                    SendInline = rights.GetValue("SendInline", false).AsBoolean,
                    EmbedLinks = rights.GetValue("EmbedLinks", false).AsBoolean,
                    SendPolls = rights.GetValue("SendPolls", false).AsBoolean,
                    ChangeInfo = rights.GetValue("ChangeInfo", false).AsBoolean,
                    InviteUsers = rights.GetValue("InviteUsers", false).AsBoolean,
                    PinMessages = rights.GetValue("PinMessages", false).AsBoolean,
                    ManageTopics = rights.GetValue("ManageTopics", false).AsBoolean,
                    SendPhotos = rights.GetValue("SendPhotos", false).AsBoolean,
                    SendVideos = rights.GetValue("SendVideos", false).AsBoolean,
                    SendRoundvideos = rights.GetValue("SendRoundvideos", false).AsBoolean,
                    SendAudios = rights.GetValue("SendAudios", false).AsBoolean,
                    SendVoices = rights.GetValue("SendVoices", false).AsBoolean,
                    SendDocs = rights.GetValue("SendDocs", false).AsBoolean,
                    SendPlain = rights.GetValue("SendPlain", false).AsBoolean,
                    UntilDate = rights.GetValue("UntilDate", 0).AsInt32
                };
            }

            chats.Add(new TChannel
            {
                Id = channel["ChannelId"].AsInt64,
                AccessHash = channel["AccessHash"].AsInt64,
                Title = channel["Title"].AsString,
                Username = channel.Contains("UserName") ? channel["UserName"].AsString : null,
                Photo = new TChatPhotoEmpty(),
                Date = channel.Contains("Date") ? channel["Date"].AsInt32 : 0,
                RestrictionReason = new TVector<IRestrictionReason>(),
                Broadcast = broadcast,
                Megagroup = megagroup,
                AdminRights = adminRights,
                DefaultBannedRights = defaultBannedRights,
                ParticipantsCount = channel.Contains("ParticipantsCount") ? channel["ParticipantsCount"].AsInt32 : 0,
                Verified = channel.Contains("Verified") && channel["Verified"].AsBoolean,
                Scam = channel.Contains("Scam") && channel["Scam"].AsBoolean,
                Fake = channel.Contains("Fake") && channel["Fake"].AsBoolean
            });
        }

        return new TMyBoosts
        {
            MyBoosts = new TVector<IMyBoost>(myBoosts),
            Chats = new TVector<IChat>(chats),
            Users = new TVector<IUser>()
        };
    }
}