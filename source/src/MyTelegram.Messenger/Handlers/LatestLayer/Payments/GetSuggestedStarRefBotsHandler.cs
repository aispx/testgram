using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain a list of suggested <a href="https://corefork.telegram.org/api/bots/webapps">mini apps</a> with available <a href="https://corefork.telegram.org/api/bots/referrals">affiliate programs</a><code>order_by_revenue</code> and <code>order_by_date</code> are mutually exclusive: if neither is set, results are sorted by profitability.
/// Possible errors
/// Code Type Description
/// 403 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getSuggestedStarRefBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSuggestedStarRefBotsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetSuggestedStarRefBots, MyTelegram.Schema.Payments.ISuggestedStarRefBots>
{
    protected override async Task<MyTelegram.Schema.Payments.ISuggestedStarRefBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetSuggestedStarRefBots obj)
    {
        // Validate peer
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Get active affiliate programs (no end_date or end_date in future)
        var programsCol = mongoDatabase.GetCollection<BsonDocument>("star_ref_programs");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Not(Builders<BsonDocument>.Filter.Exists("end_date")),
            Builders<BsonDocument>.Filter.Eq("end_date", BsonNull.Value),
            Builders<BsonDocument>.Filter.Gt("end_date", now)
        );

        // Determine sort order
        SortDefinition<BsonDocument> sort;
        if (obj.OrderByRevenue)
        {
            // Sort by estimated revenue (commission * daily_revenue_per_user)
            sort = Builders<BsonDocument>.Sort.Descending("commission_permille");
        }
        else if (obj.OrderByDate)
        {
            sort = Builders<BsonDocument>.Sort.Descending("created_at");
        }
        else
        {
            // Default: sort by profitability (commission)
            sort = Builders<BsonDocument>.Sort.Descending("commission_permille");
        }

        var programs = await programsCol.Find(filter)
            .Sort(sort)
            .Skip(int.TryParse(obj.Offset, out var skip) ? skip : 0)
            .Limit(obj.Limit)
            .ToListAsync();

        if (programs.Count == 0)
        {
            return new MyTelegram.Schema.Payments.TSuggestedStarRefBots
            {
                Count = 0,
                SuggestedBots = new TVector<IStarRefProgram>(),
                Users = new TVector<IUser>()
            };
        }

        // Get bot users
        var botIds = programs.Select(p => p["bot_id"].AsInt64).ToList();
        var users = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(botIds));

        // Build suggested programs
        var suggestedBots = new TVector<IStarRefProgram>();
        foreach (var program in programs)
        {
            long dailyRevenue = 100; // Default estimate
            if (program.Contains("daily_revenue_per_user") && !program["daily_revenue_per_user"].IsBsonNull)
            {
                var revenueValue = program["daily_revenue_per_user"];
                dailyRevenue = revenueValue.BsonType == BsonType.Int64
                    ? revenueValue.AsInt64
                    : revenueValue.AsInt32;
            }

            suggestedBots.Add(new TStarRefProgram
            {
                BotId = program["bot_id"].AsInt64,
                CommissionPermille = program["commission_permille"].AsInt32,
                DurationMonths = program.Contains("duration_months") ? program["duration_months"].AsInt32 : null,
                DailyRevenuePerUser = new TStarsAmount
                {
                    Amount = dailyRevenue,
                    Nanos = 0
                }
            });
        }

        var userList = new TVector<IUser>();
        foreach (var user in users)
        {
            userList.Add(StarRefBotUserHelper.BuildBotUser(userConverterService, input, user));
        }

        var nextOffset = programs.Count >= obj.Limit
            ? (int.Parse(obj.Offset ?? "0") + obj.Limit).ToString()
            : null;

        return new MyTelegram.Schema.Payments.TSuggestedStarRefBots
        {
            Count = suggestedBots.Count,
            SuggestedBots = suggestedBots,
            Users = userList,
            NextOffset = nextOffset
        };
    }
}
