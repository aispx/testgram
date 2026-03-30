using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Handles Stripe payment confirmation.
/// The client sends the Stripe PaymentIntent ID in the receipt JSON: {"paymentIntentId":"pi_xxx"} or {"formId":123}
/// </summary>
internal sealed class AssignPlayMarketTransactionHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestAssignPlayMarketTransaction obj)
    {
        var receiptJson = obj.Receipt?.Data ?? "{}";
        string? paymentIntentId = null;
        long formId = 0;

        try
        {
            var doc = JsonDocument.Parse(receiptJson).RootElement;
            if (doc.TryGetProperty("paymentIntentId", out var piProp))
                paymentIntentId = piProp.GetString();
            if (doc.TryGetProperty("formId", out var fProp))
                formId = fProp.GetInt64();
        }
        catch { }

        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");

        StripePaymentIntentDocument? intent = null;
        if (!string.IsNullOrEmpty(paymentIntentId))
            intent = await col.Find(x => x.PaymentIntentId == paymentIntentId).FirstOrDefaultAsync();
        else if (formId != 0)
            intent = await col.Find(x => x.FormId == formId && x.UserId == input.UserId).FirstOrDefaultAsync();

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

        // Cleanup
        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return new TUpdates { Updates = [], Users = [], Chats = [], Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 };
    }
}
