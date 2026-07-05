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

        var savedDoc = await savedCol.Find(d => d.IsUnique && d.UniqueSlug == doc.Slug).FirstOrDefaultAsync();
        var currentTime = DateTime.UtcNow.ToTimestamp();
        var lockedUntil = LaterTimestamp(doc.TransferLockedUntil, savedDoc?.CanResellAt, savedDoc?.CanTransferAt, savedDoc?.CanCraftAt);
        var listingRequested = obj.ResellAmount switch
        {
            TStarsAmount starsAmount => starsAmount.Amount > 0,
            TStarsTonAmount tonAmount => tonAmount.Amount > 0,
            _ => false
        };
        if (listingRequested && lockedUntil.HasValue && lockedUntil.Value > currentTime)
        {
            var secondsToWait = lockedUntil.Value - currentTime;
            throw new RpcException(new RpcError(400, $"STARGIFT_RESELL_TOO_EARLY_{secondsToWait}"));
        }

        // Layer 206: obj.ResellAmount may be a TStarsAmount (Stars pricing) or
        // a TStarsTonAmount (TON pricing). Zero/absent means "delist". We only
        // touch the column corresponding to the submitted currency so the
        // seller can list the gift in both currencies simultaneously.
        //
        // Item 1: when the seller lists *only* for TON (no prior Stars price), mark the
        // gift as ResaleTonOnly so the buy form picks the TON branch instead of falling
        // into the Stars branch with ResellStars=0 and rejecting with STARGIFT_INVALID.
        // When the seller (re-)lists in Stars, clear ResaleTonOnly so both currencies
        // remain available.
        var update = Builders<UniqueStarGiftDocument>.Update;
        switch (obj.ResellAmount)
        {
            case TStarsTonAmount tonAmount:
            {
                var setTonOnly = tonAmount.Amount > 0 && doc.ResellStars <= 0;
                var u = update.Set(d => d.ResellTon, tonAmount.Amount);
                if (setTonOnly) u = u.Set(d => d.ResaleTonOnly, true);
                if (tonAmount.Amount <= 0) u = u.Set(d => d.ResaleTonOnly, false);
                await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId, u);
                break;
            }
            case TStarsAmount starsAmount:
                if (doc.ResaleTonOnly && starsAmount.Amount > 0)
                {
                    // Switching back to a Stars listing automatically clears the
                    // TON-only flag instead of rejecting with STARGIFT_INVALID.
                    await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                        update.Set(d => d.ResellStars, starsAmount.Amount).Set(d => d.ResaleTonOnly, false));
                }
                else
                {
                    await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                        update.Set(d => d.ResellStars, starsAmount.Amount));
                }
                break;
            default:
                // Delist from both currencies if an empty/unknown amount is passed.
                await uniqueCol.UpdateOneAsync(d => d.UniqueId == doc.UniqueId,
                    update.Set(d => d.ResellStars, 0L).Set(d => d.ResellTon, 0L).Set(d => d.ResaleTonOnly, false));
                break;
        }

        // The cooldown field means "can't resell before this date"; keep it aligned
        // with the unique gift transfer lock instead of using listing state.
        var activeCooldownAt = lockedUntil.HasValue && lockedUntil.Value > currentTime ? lockedUntil : null;
        await savedCol.UpdateOneAsync(
            d => d.IsUnique && d.UniqueSlug == doc.Slug,
            Builders<SavedStarGiftDocument>.Update
                .Set(d => d.CanTransferAt, activeCooldownAt)
                .Set(d => d.CanResellAt, activeCooldownAt)
                .Set(d => d.CanCraftAt, activeCooldownAt)
        );

        return new TUpdates { Chats = new TVector<IChat>(), Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Date = CurrentDate };
    }

    private static int? LaterTimestamp(params int?[] values)
    {
        int? result = null;
        foreach (var value in values)
        {
            if (value.HasValue && (!result.HasValue || value.Value > result.Value))
                result = value.Value;
        }

        return result;
    }
}
