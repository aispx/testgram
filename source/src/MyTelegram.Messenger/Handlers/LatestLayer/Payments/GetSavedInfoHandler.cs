using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetSavedInfoHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetSavedInfo, MyTelegram.Schema.Payments.ISavedInfo>
{
    protected override async Task<MyTelegram.Schema.Payments.ISavedInfo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetSavedInfo obj)
    {
        var col = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
        var cards = await col.Find(x => x.UserId == input.UserId).ToListAsync();

        return new MyTelegram.Schema.Payments.TSavedInfo
        {
            HasSavedCredentials = cards.Count > 0,
        };
    }
}
