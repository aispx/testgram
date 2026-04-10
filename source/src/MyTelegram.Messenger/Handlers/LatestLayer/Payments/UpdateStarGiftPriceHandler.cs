using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class UpdateStarGiftPriceHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpdateStarGiftPrice, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpdateStarGiftPrice obj)
    {
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        UniqueStarGiftDocument? doc = null;

        switch (obj.Stargift)
        {
            case TInputSavedStarGiftUser u:
                // MsgId is MessageId in saved-star-gifts for regular gifts, or RandomId for unique
                var saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique &&
                    (d.MessageId == u.MsgId || d.RandomId == u.MsgId)).FirstOrDefaultAsync();
                if (saved?.UniqueSlug != null)
                    doc = await uniqueCol.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync();
                break;
            case TInputSavedStarGiftSlug s:
                doc = await uniqueCol.Find(d => d.Slug == s.Slug && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
                break;
            case TInputSavedStarGiftChat c:
                var savedChat = await savedCol.Find(d => d.IsUnique && d.RandomId == c.SavedId).FirstOrDefaultAsync();
                if (savedChat?.UniqueSlug != null)
                    doc = await uniqueCol.Find(d => d.Slug == savedChat.UniqueSlug).FirstOrDefaultAsync();
                break;
        }

        if (doc == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        // Check if gift was burned (used in crafting)
        if (doc!.Burned)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));

        if (doc!.OwnerUserId != input.UserId && doc.OwnerChannelId == 0) RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

        long resellStars = obj.ResellAmount is TStarsAmount sa ? sa.Amount : 0;

        await uniqueCol.UpdateOneAsync(
            d => d.UniqueId == doc.UniqueId,
            Builders<UniqueStarGiftDocument>.Update.Set(d => d.ResellStars, resellStars)
        );

        await savedCol.UpdateOneAsync(
            d => d.IsUnique && d.UniqueSlug == doc.Slug,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.CanResellAt, resellStars > 0 ? 0 : (int?)null)
        );

        return new TUpdates { Chats = new TVector<IChat>(), Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Date = CurrentDate };
    }
}
