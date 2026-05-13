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

        // Layer 206: obj.ResellAmount may be a TStarsAmount (Stars pricing) or
        // a TStarsTonAmount (TON pricing). Zero/absent means "delist". We only
        // touch the column corresponding to the submitted currency so the
        // seller can list the gift in both currencies simultaneously.
        var update = Builders<UniqueStarGiftDocument>.Update;
        switch (obj.ResellAmount)
        {
            case TStarsTonAmount tonAmount:
                await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                    update.Set(d => d.ResellTon, tonAmount.Amount));
                break;
            case TStarsAmount starsAmount:
                if (doc.ResaleTonOnly && starsAmount.Amount > 0)
                    RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
                await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                    update.Set(d => d.ResellStars, starsAmount.Amount));
                break;
            default:
                // Delist from both currencies if an empty/unknown amount is passed.
                await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                    update.Set(d => d.ResellStars, 0L).Set(d => d.ResellTon, 0L));
                break;
        }

        // Refresh CanResellAt on the saved-star-gift record based on the new combined state.
        var refreshed = await uniqueCol.Find(d => d.UniqueId == doc.UniqueId).FirstOrDefaultAsync();
        var anyListing = (refreshed?.ResellStars ?? 0) > 0 || (refreshed?.ResellTon ?? 0) > 0;
        await savedCol.UpdateOneAsync(
            d => d.IsUnique && d.UniqueSlug == doc.Slug,
            Builders<SavedStarGiftDocument>.Update.Set(d => d.CanResellAt, anyListing ? 0 : (int?)null)
        );

        return new TUpdates { Chats = new TVector<IChat>(), Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Date = CurrentDate };
    }
}
