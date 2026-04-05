using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Disable or re-enable Telegram ads for the current <a href="https://corefork.telegram.org/api/premium">Premium</a> account.Useful for business owners that may want to launch and view their own Telegram ads via the <a href="https://ads.telegram.org/">Telegram ad platform »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.toggleSponsoredMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 223: account.toggleSponsoredMessages#b9d9a38d enabled:Bool = Bool;
/// </remarks>
internal sealed class ToggleSponsoredMessagesHandler(
    IMongoDatabase database,
    ILogger<ToggleSponsoredMessagesHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestToggleSponsoredMessages, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestToggleSponsoredMessages obj)
    {
        // Get user settings
        var collection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
        var user = await collection.Find(filter).FirstOrDefaultAsync();

        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Check if user has Premium
        bool isPremium = user.Contains("IsPremium") && user["IsPremium"].AsBoolean;

        // Update sponsored messages setting
        var update = Builders<BsonDocument>.Update.Set("SponsoredMessagesEnabled", obj.Enabled);
        await collection.UpdateOneAsync(filter, update);

        logger.LogInformation("Sponsored messages toggled: UserId={UserId}, Enabled={Enabled}, IsPremium={IsPremium}",
            input.UserId, obj.Enabled, isPremium);

        return new TBoolTrue();
    }
}