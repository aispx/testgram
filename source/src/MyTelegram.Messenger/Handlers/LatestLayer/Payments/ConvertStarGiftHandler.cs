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
        var gift = await collection.Find(
            Builders<SavedStarGiftDocument>.Filter.And(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId),
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.MessageId, msgId)
            )
        ).FirstOrDefaultAsync();

        if (gift == null)
            throw new RpcException(new RpcError(400, "STARGIFT_NOT_FOUND"));

        // Check if already converted
        if (gift.ConvertStars == 0)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_CONVERTED"));

        // Check if this is a unique gift that was burned
        if (gift.IsUnique && !string.IsNullOrEmpty(gift.UniqueSlug))
        {
            var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
            var uniqueDoc = await uniqueCol.Find(d => d.Slug == gift.UniqueSlug).FirstOrDefaultAsync();
            if (uniqueDoc?.Burned == true)
                throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));
        }

        // Delete the gift and credit stars
        await collection.DeleteOneAsync(
            Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Id, gift.Id)
        );

        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, gift.ConvertStars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, gift.ConvertStars, gift: true,
            peerUserId: gift.FromUserId > 0 ? gift.FromUserId : null);

        return new TBoolTrue();
    }
}
