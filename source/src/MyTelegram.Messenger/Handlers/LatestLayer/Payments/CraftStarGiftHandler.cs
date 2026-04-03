using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Craft star gift by combining 3-4 identical gifts
/// From madrik1337/telelakel findings:
/// - Can only craft SAME gift type (4 cakes, not 3 cakes + 1 lollipop)
/// - Number taken from FIRST SLOT gift (not random!)
/// - Blockchain gifts CAN be used, but NOT in first slot (number won't transfer)
/// - Craft can FAIL - gifts burn without result
/// - Supply decreases by number of gifts used
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.craftStarGift"/> </c></para>
/// </summary>
internal sealed class CraftStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPtsHelper ptsHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCraftStarGift, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCraftStarGift obj)
    {
        if (obj.Stargift == null || obj.Stargift.Count < 3 || obj.Stargift.Count > 4)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        // Check craft cooldown on first gift
        var currentTime = DateTime.UtcNow.ToTimestamp();
        SavedStarGiftDocument? firstSaved = null;
        if (obj.Stargift[0] is TInputSavedStarGiftUser firstUser)
        {
            firstSaved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique && (d.MessageId == firstUser.MsgId || d.RandomId == firstUser.MsgId)).FirstOrDefaultAsync();
        }
        else if (obj.Stargift[0] is TInputSavedStarGiftSlug firstSlug)
        {
            firstSaved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique && d.UniqueSlug == firstSlug.Slug).FirstOrDefaultAsync();
        }
        else if (obj.Stargift[0] is TInputSavedStarGiftChat firstChat)
        {
            firstSaved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique && d.RandomId == firstChat.SavedId).FirstOrDefaultAsync();
        }

        if (firstSaved?.CanCraftAt.HasValue == true && firstSaved.CanCraftAt.Value > currentTime)
        {
            var secondsToWait = firstSaved.CanCraftAt.Value - currentTime;
            throw new RpcException(new RpcError(400, $"STARGIFT_CRAFT_TOO_EARLY_{secondsToWait}"));
        }

        // Load all gifts to craft
        var gifts = new List<UniqueStarGiftDocument>();
        foreach (var input_gift in obj.Stargift)
        {
            UniqueStarGiftDocument? unique = null;

            if (input_gift is TInputSavedStarGiftUser u)
            {
                var saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique && (d.MessageId == u.MsgId || d.RandomId == u.MsgId)).FirstOrDefaultAsync();
                if (saved != null)
                    unique = await uniqueCol.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftSlug s)
            {
                unique = await uniqueCol.Find(d => d.Slug == s.Slug && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftChat c)
            {
                unique = await uniqueCol.Find(d => d.UniqueId == c.SavedId && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
            }

            if (unique == null)
            {
                // Check if gift was already burned (used in previous craft)
                throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));
            }
            gifts.Add(unique);
        }

        // Validate: all gifts must be same GiftId (can't mix different gifts)
        var firstGiftId = gifts[0].GiftId;
        if (gifts.Any(g => g.GiftId != firstGiftId))
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Validate: cannot craft already crafted gifts
        if (gifts.Any(g => g.Crafted))
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_CRAFTED"));

        // Validate: first slot cannot be blockchain gift (number won't transfer)
        // From telelakel: "blockchain gifts can't be placed in the first slot"
        if (gifts[0].WasOnBlockchain)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Get gift definition
        var giftDoc = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == firstGiftId).FirstOrDefaultAsync();
        if (giftDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Calculate success chance (more gifts = better chance)
        // From madrik1337: craft can fail, gifts burn without result
        var successChance = obj.Stargift.Count == 4 ? 0.85 : 0.60; // 85% for 4 gifts, 60% for 3
        var craftSucceeded = Random.Shared.NextDouble() < successChance;

        // Delete all source gifts (they burn regardless of success)
        foreach (var gift in gifts)
        {
            // Mark as burned (Layer 223+)
            gift.Burned = true;
            gift.OwnerUserId = 0; // Remove owner
            await uniqueCol.ReplaceOneAsync(d => d.UniqueId == gift.UniqueId, gift);
            await savedCol.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == gift.Slug);
        }

        // Update supply: decrease by number of gifts used
        await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .UpdateOneAsync(
                d => d.GiftId == firstGiftId,
                Builders<StarGiftDocument>.Update.Inc(d => d.AvailabilityRemains, -obj.Stargift.Count)
            );

        var now = DateTime.UtcNow.ToTimestamp();

        if (!craftSucceeded)
        {
            // Craft failed - gifts burned, no result
            return new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 };
        }

        // Craft succeeded - use FIRST SLOT gift as base
        // Number from first slot is kept (not random!)
        var firstGift = gifts[0];

        // Generate NEW crafted attributes with higher rarity
        var newAttrs = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, giftDoc!, crafted: true);

        // Calculate total Initial Price from all gifts used in craft
        var totalInitialPrice = gifts.Sum(g => g.InitialSaleStars);

        // Update first gift with new crafted attributes
        // Keep: UniqueId, Slug, Num (from FIRST slot), GiftId
        firstGift.Attributes = newAttrs;
        firstGift.Date = now;
        firstGift.TransferLockedUntil = null; // Can transfer immediately
        firstGift.Crafted = true; // Mark as crafted (Layer 223+)
        firstGift.CraftChancePermille = (int)(successChance * 1000); // Store craft chance
        firstGift.InitialSaleStars = totalInitialPrice; // Sum of all gifts' Initial Price

        await uniqueCol.InsertOneAsync(firstGift);

        // Re-create saved gift entry
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            OwnerUserId = input.UserId,
            FromUserId = firstGift.FromUserId,
            GiftId = firstGift.GiftId,
            Stars = totalInitialPrice, // Sum of all gifts' Initial Price
            IsUnique = true,
            UniqueSlug = firstGift.Slug,
            RandomId = firstGift.UniqueId,
            Saved = true,
            Date = now,
            GiftNum = firstGift.Num, // Number from FIRST slot
            DocumentId = firstGift.DocumentId,
            DocumentAccessHash = firstGift.DocumentAccessHash,
            FileReference = firstGift.FileReference,
            DocumentDate = firstGift.DocumentDate,
            MimeType = firstGift.MimeType,
            DocumentSize = firstGift.DocumentSize,
            DcId = firstGift.DcId,
        });

        return new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 };
    }
}