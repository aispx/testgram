using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetSavedInfoHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetSavedInfo, MyTelegram.Schema.Payments.ISavedInfo>
{
    protected override async Task<MyTelegram.Schema.Payments.ISavedInfo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetSavedInfo obj)
    {
        var credentialsCol = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
        var cards = await credentialsCol.Find(x => x.UserId == input.UserId).ToListAsync();

        var infoCol = mongoDatabase.GetCollection<SavedPaymentRequestedInfoDocument>("saved-payment-requested-info");
        var savedInfo = await infoCol.Find(x => x.UserId == input.UserId).FirstOrDefaultAsync();

        IPaymentRequestedInfo? requestedInfo = null;
        if (savedInfo?.Info is { Length: > 0 })
        {
            var buffer = new ReadOnlyMemory<byte>(savedInfo.Info);
            requestedInfo = buffer.Read<IPaymentRequestedInfo>();
        }

        return new MyTelegram.Schema.Payments.TSavedInfo
        {
            HasSavedCredentials = cards.Count > 0,
            SavedInfo = requestedInfo,
        };
    }
}
