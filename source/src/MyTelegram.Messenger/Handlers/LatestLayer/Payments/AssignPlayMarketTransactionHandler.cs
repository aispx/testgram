using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Services.Services;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Handles Stripe payment confirmation for Stars and Premium purchases.
/// The client sends the Stripe PaymentIntent ID in the receipt JSON: {"paymentIntentId":"pi_xxx"} or {"formId":123}
/// For Premium: {"purpose":"premium"}
/// </summary>
internal sealed class AssignPlayMarketTransactionHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction obj)
    {
        var receiptJson = obj.Receipt?.Data ?? "{}";
        string? paymentIntentId = null;
        long formId = 0;
        string? purpose = null;

        try
        {
            var doc = JsonDocument.Parse(receiptJson).RootElement;
            if (doc.TryGetProperty("paymentIntentId", out var piProp))
                paymentIntentId = piProp.GetString();
            if (doc.TryGetProperty("formId", out var fProp))
                formId = fProp.GetInt64();
            if (doc.TryGetProperty("purpose", out var purposeProp))
                purpose = purposeProp.GetString();
        }
        catch { }

        // Handle Premium purchase (no payment intent needed for Testgram)
        if (purpose == "premium")
        {
            await ActivatePremiumAsync(input.UserId);
            return new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 };
        }

        // Handle Stars purchase (existing logic)
        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");

        StripePaymentIntentDocument? intent = null;
        if (!string.IsNullOrEmpty(paymentIntentId))
            intent = await col.Find(x => x.PaymentIntentId == paymentIntentId).FirstOrDefaultAsync();
        else if (formId != 0)
            intent = await col.Find(x => x.FormId == formId && x.UserId == input.UserId).FirstOrDefaultAsync();

        // For Testgram: if no intent found and no purpose specified, allow Stars purchase anyway
        if (intent == null && string.IsNullOrEmpty(purpose))
        {
            // Free Stars for testing
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, 1000);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, 1000,
                title: "Test top-up: 1000 stars");
            await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
            return new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 };
        }

        if (intent == null)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        // Verify with Stripe
        var stripe = options.Value.Stripe;
        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent!.PaymentIntentId);
            if (status != "succeeded")
                RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        }

        // Credit stars
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, intent!.Stars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, intent.Stars,
            title: $"Stripe top-up: {intent.Stars} stars");
        await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);

        // Cleanup
        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 };
    }

    private async Task ActivatePremiumAsync(long userId)
    {
        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");

        // Check if user already has Premium
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCol.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        bool alreadyHasPremium = user.Contains("Premium") && user["Premium"].AsBoolean;

        // Set Premium flag
        var update = Builders<BsonDocument>.Update.Set("Premium", true);
        await userCol.UpdateOneAsync(userFilter, update);

        // Grant 4 boosts ONLY if user didn't have Premium before
        if (!alreadyHasPremium)
        {
            var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expires = now + (86400 * 365); // 1 year

            // Find next available slot
            var existingBoosts = await boostCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
                .ToListAsync();

            int nextSlot = 1;
            if (existingBoosts.Count > 0)
            {
                var usedSlots = existingBoosts.Select(b => b["Slot"].AsInt32).ToHashSet();
                while (usedSlots.Contains(nextSlot))
                    nextSlot++;
            }

            // Add 4 boosts
            for (int i = 0; i < 4; i++)
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
    }
}
