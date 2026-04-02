using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Returns a set of useful suggestions and PSA/MTProxy sponsored peers, see <a href="https://corefork.telegram.org/api/config#suggestions">here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getPromoData"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPromoDataHandler : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetPromoData, MyTelegram.Schema.Help.IPromoData>
{
    private readonly IMongoDatabase _database;

    public GetPromoDataHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IPromoData> HandleCoreAsync(IRequestInput input, RequestGetPromoData obj)
    {
        // Get xiegram channel by username from MongoDB
        var collection = _database.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserName", "xiegram");
        var channelDoc = await collection.Find(filter).FirstOrDefaultAsync();

        if (channelDoc == null)
        {
            // Channel not found, return empty
            return new TPromoDataEmpty
            {
                Expires = int.MaxValue
            };
        }

        // Extract channel data
        var channelId = channelDoc["ChannelId"].AsInt64;
        var accessHash = channelDoc["AccessHash"].AsInt64;
        var title = channelDoc["Title"].AsString;
        var username = channelDoc["UserName"].AsString;

        // Return promo data with xiegram channel (PSA style - shows banner at top)
        var channelObj = new TChannel
        {
            Id = channelId,
            AccessHash = accessHash,
            Title = title,
            Username = username,
            Broadcast = channelDoc.GetValue("Broadcast", false).AsBoolean,
            Megagroup = channelDoc.GetValue("MegaGroup", false).AsBoolean,
            Verified = channelDoc.GetValue("Verified", false).AsBoolean,
            Scam = channelDoc.GetValue("Scam", false).AsBoolean,
            Fake = channelDoc.GetValue("Fake", false).AsBoolean,
            Date = channelDoc.GetValue("Date", 0).AsInt32,
            ParticipantsCount = channelDoc.GetValue("ParticipantsCount", 0).AsInt32,
            // Add required fields to prevent NullReferenceException
            Photo = new TChatPhotoEmpty(),
            RestrictionReason = new TVector<IRestrictionReason>()
        };

        return new TPromoData
        {
            Expires = int.MaxValue, // Never expires
            Peer = new TPeerChannel { ChannelId = channelId },
            // No PsaType/PsaMessage = PROMO_TYPE_OTHER (shows below folders, not as banner)
            Chats = new TVector<IChat> { channelObj },
            Users = new TVector<IUser>(),
            PendingSuggestions = new TVector<string>(),
            DismissedSuggestions = new TVector<string>()
        };
    }
}