using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ConvertStarGiftHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestConvertStarGift, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestConvertStarGift obj)
    {
        var collection = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var chatChannelId = obj.Stargift is TInputSavedStarGiftChat c0 && c0.Peer is TInputPeerChannel ch0
            ? ch0.ChannelId
            : 0;

        SavedStarGiftDocument? gift = obj.Stargift switch
        {
            TInputSavedStarGiftUser u when u.MsgId != 0 => await collection.Find(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId)
                & SavedStarGiftMsgIdHelper.MatchMsgId(u.MsgId)).FirstOrDefaultAsync(),
            TInputSavedStarGiftSlug s => await collection.Find(d =>
                d.OwnerUserId == input.UserId && d.UniqueSlug == s.Slug).FirstOrDefaultAsync(),
            TInputSavedStarGiftChat c => await collection.Find(d =>
                d.OwnerChannelId == chatChannelId &&
                d.RandomId == c.SavedId).FirstOrDefaultAsync(),
            _ => null
        };

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
