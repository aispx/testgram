using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ClearSavedInfoHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestClearSavedInfo, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestClearSavedInfo obj)
    {
        if (obj.Credentials)
        {
            var col = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
            await col.DeleteManyAsync(x => x.UserId == input.UserId);
        }

        if (obj.Info)
        {
            var col = mongoDatabase.GetCollection<SavedPaymentRequestedInfoDocument>("saved-payment-requested-info");
            await col.DeleteOneAsync(x => x.UserId == input.UserId);
        }

        return new TBoolTrue();
    }
}
