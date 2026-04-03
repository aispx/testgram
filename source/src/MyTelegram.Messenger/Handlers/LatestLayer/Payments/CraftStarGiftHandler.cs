using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Craft star gift by combining 3-4 identical gifts
/// From madrik1337/telelakel findings:
/// - Can only craft SAME gift type (4 cakes, not 3 cakes + 1 lollipop)
/// - Number taken RANDOMLY from used gifts (not first slot!)
/// - Craft can FAIL - gifts burn without result
/// - Supply decreases by number of gifts used
/// - Blockchain gifts CANNOT be used at all
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

            if (unique == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
            gifts.Add(unique);
        }

        // Validate: all gifts must be same GiftId (can't mix different gifts)
        var firstGiftId = gifts[0].GiftId;
        if (gifts.Any(g => g.GiftId != firstGiftId))
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Validate: no blockchain gifts allowed at all
        if (gifts.Any(g => g.WasOnBlockchain))
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
            await uniqueCol.DeleteOneAsync(d => d.UniqueId == gift.UniqueId);
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

        // Craft succeeded - pick random gift to keep
        var selectedGift = gifts[Random.Shared.Next(gifts.Count)];

        // Generate NEW crafted attributes with higher rarity
        var newAttrs = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, giftDoc!, crafted: true);

        // Update selected gift with new crafted attributes
        // Keep: UniqueId, Slug, Num (from randomly selected gift), GiftId
        selectedGift.Attributes = newAttrs;
        selectedGift.Date = now;
        selectedGift.TransferLockedUntil = null; // Can transfer immediately

        await uniqueCol.InsertOneAsync(selectedGift);

        // Re-create saved gift entry
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            OwnerUserId = input.UserId,
            FromUserId = selectedGift.FromUserId,
            GiftId = selectedGift.GiftId,
            Stars = 0,
            IsUnique = true,
            UniqueSlug = selectedGift.Slug,
            RandomId = selectedGift.UniqueId,
            Saved = true,
            Date = now,
            GiftNum = selectedGift.Num, // Number from randomly selected gift
            DocumentId = selectedGift.DocumentId,
            DocumentAccessHash = selectedGift.DocumentAccessHash,
            FileReference = selectedGift.FileReference,
            DocumentDate = selectedGift.DocumentDate,
            MimeType = selectedGift.MimeType,
            DocumentSize = selectedGift.DocumentSize,
            DcId = selectedGift.DcId,
        });

        return new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 };
    }
}