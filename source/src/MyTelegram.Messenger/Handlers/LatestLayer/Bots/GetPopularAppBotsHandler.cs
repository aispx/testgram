using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Fetch popular <a href="https://corefork.telegram.org/api/bots/webapps#main-mini-apps">Main Mini Apps</a>, to be used in the <a href="https://corefork.telegram.org/api/search#apps-tab">apps tab of global search »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.getPopularAppBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPopularAppBotsHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestGetPopularAppBots, MyTelegram.Schema.Bots.IPopularAppBots>
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Bots.IPopularAppBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestGetPopularAppBots obj)
    {
        var limit = obj.Limit <= 0 ? DefaultLimit : Math.Min(obj.Limit, MaxLimit);

        // offset is an opaque string; we page by the number of bots already returned.
        var skip = 0;
        if (!string.IsNullOrEmpty(obj.Offset) && int.TryParse(obj.Offset, out var parsedOffset) && parsedOffset > 0)
        {
            skip = parsedOffset;
        }

        // A "Main Mini App" bot is a bot with BotHasMainApp set, which is what makes clients show
        // the "Open App" button. Ranked by active users, most popular first.
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Bot", true),
            Builders<BsonDocument>.Filter.Eq("BotHasMainApp", true),
            Builders<BsonDocument>.Filter.Ne("IsDeleted", true));

        var docs = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort
                .Descending("BotActiveUsers")
                .Ascending("UserId"))
            .Skip(skip)
            .Limit(limit)
            .Project(Builders<BsonDocument>.Projection.Include("UserId"))
            .ToListAsync();

        var botIds = docs.Select(p => GetInt64(p.GetValue("UserId", BsonNull.Value)))
            .Where(p => p != 0)
            .ToList();

        var users = botIds.Count == 0
            ? []
            : await userConverterService.GetUserListAsync(input, botIds, layer: input.Layer);

        return new MyTelegram.Schema.Bots.TPopularAppBots
        {
            Users = [.. users],
            // Only advertise another page when this one was filled.
            NextOffset = docs.Count == limit ? (skip + docs.Count).ToString() : null
        };
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            _ => 0
        };
    }
}
