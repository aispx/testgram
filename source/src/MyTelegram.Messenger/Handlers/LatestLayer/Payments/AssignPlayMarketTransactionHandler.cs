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
            await StoreTransactionHelper.ActivatePremiumAsync(mongoDatabase, input.UserId);
            return StoreTransactionHelper.EmptyUpdates();
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
            await StoreTransactionHelper.CreditStarsAsync(
                mongoDatabase, objectMessageSender, input.UserId, 1000, "Test top-up: 1000 stars");
            return StoreTransactionHelper.EmptyUpdates();
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
        await StoreTransactionHelper.CreditStarsAsync(
            mongoDatabase, objectMessageSender, input.UserId, intent!.Stars, $"Stripe top-up: {intent.Stars} stars");

        // Cleanup
        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return StoreTransactionHelper.EmptyUpdates();
    }
}
