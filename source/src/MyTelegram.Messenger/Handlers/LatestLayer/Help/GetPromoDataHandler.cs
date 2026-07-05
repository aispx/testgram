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
    private readonly IChatConverterService _chatConverterService;

    public GetPromoDataHandler(IMongoDatabase database, IChatConverterService chatConverterService)
    {
        _database = database;
        _chatConverterService = chatConverterService;
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

        var channelId = channelDoc["ChannelId"].AsInt64;
        var channelObj = await _chatConverterService.GetChannelAsync(input, channelId, false, false, input.Layer);

        return new TPromoData
        {
            Expires = DateTime.UtcNow.AddHours(1).ToTimestamp(),
            Peer = new TPeerChannel { ChannelId = channelId },
            PsaType = "premium_last_day",
            PsaMessage = "Сегодня — последняя возможность оплатить Telegram Premium.",
            Chats = new TVector<IChat> { channelObj },
            Users = new TVector<IUser>(),
            PendingSuggestions = new TVector<string>(),
            DismissedSuggestions = new TVector<string>()
        };
    }
}
