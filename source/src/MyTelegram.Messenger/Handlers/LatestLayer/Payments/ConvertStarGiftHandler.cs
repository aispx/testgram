using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ConvertStarGiftHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestConvertStarGift, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestConvertStarGift obj)
    {
        if (obj.Stargift is not TInputSavedStarGiftUser u || u.MsgId == 0)
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();

        var msgId = ((TInputSavedStarGiftUser)obj.Stargift).MsgId;
        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var gift = await collection.FindOneAndDeleteAsync(
            Builders<SavedStarGiftDocument>.Filter.And(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId),
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.MessageId, msgId)
            )
        );

        if (gift != null)
        {
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, gift.ConvertStars);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, gift.ConvertStars, gift: true,
                peerUserId: gift.FromUserId > 0 ? gift.FromUserId : null);
        }

        return new TBoolTrue();
    }
}
