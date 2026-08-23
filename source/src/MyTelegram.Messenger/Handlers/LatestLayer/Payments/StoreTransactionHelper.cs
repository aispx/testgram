using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// The half of an app store purchase that is the same whether the receipt came from the App Store or
/// from Play: turning a verified purchase into Premium or into a Stars balance.
/// </summary>
/// <remarks>
/// See https://corefork.telegram.org/method/payments.assignAppStoreTransaction and
/// https://corefork.telegram.org/method/payments.assignPlayMarketTransaction
/// </remarks>
public static class StoreTransactionHelper
{
    public static async Task CreditStarsAsync(
        IMongoDatabase mongoDatabase,
        IObjectMessageSender objectMessageSender,
        long userId,
        long stars,
        string title)
    {
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, userId, stars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, userId, stars, title: title);
        await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, userId);
    }

    /// <summary>
    /// Marks the account Premium, granting the four boost slots that come with a first subscription.
    /// </summary>
    public static async Task ActivatePremiumAsync(IMongoDatabase mongoDatabase, long userId)
    {
        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCol.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var alreadyHasPremium = user!.Contains("Premium") && user["Premium"].AsBoolean;

        await userCol.UpdateOneAsync(userFilter, Builders<BsonDocument>.Update.Set("Premium", true));

        // The boosts come with becoming Premium, not with every renewal.
        if (alreadyHasPremium)
        {
            return;
        }

        var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = now + 86400 * 365;

        var existingBoosts = await boostCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId)).ToListAsync();

        var nextSlot = 1;
        if (existingBoosts.Count > 0)
        {
            var usedSlots = existingBoosts.Select(b => b["Slot"].AsInt32).ToHashSet();
            while (usedSlots.Contains(nextSlot))
            {
                nextSlot++;
            }
        }

        for (var i = 0; i < 4; i++)
        {
            await boostCol.InsertOneAsync(new BsonDocument
            {
                ["UserId"] = userId,
                ["Slot"] = nextSlot + i,
                ["ChannelId"] = 0L,
                ["Date"] = now,
                ["Expires"] = expires
            });
        }
    }

    public static TUpdates EmptyUpdates() => new()
    {
        Updates = new TVector<IUpdate>(),
        Users = new TVector<IUser>(),
        Chats = new TVector<IChat>(),
        Date = DateTime.UtcNow.ToTimestamp(),
        Seq = 0
    };
}
