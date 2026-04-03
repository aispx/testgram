using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Craft a new unique star gift by re-rolling attributes of existing gifts
/// From telelakel: "Combine between 1 and 4 gifts to receive a gift with a unique model"
/// IMPORTANT: Only attributes (model, pattern, backdrop) change. Gift number stays from first slot.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.craftStarGift"/> </c></para>
/// </summary>
internal sealed class CraftStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPtsHelper ptsHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCraftStarGift, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCraftStarGift obj)
    {
        if (obj.Stargift == null || obj.Stargift.Count < 1 || obj.Stargift.Count > 4)
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

        // Validate first slot: can't be blockchain gift (from telelakel)
        var firstGift = gifts[0];
        if (firstGift.WasOnBlockchain)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Get gift definition for attribute generation
        var giftDoc = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == firstGift.GiftId).FirstOrDefaultAsync();
        if (giftDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Generate NEW crafted attributes (model, pattern, backdrop)
        var newAttrs = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, giftDoc!, crafted: true);

        // Delete all source gifts (they are burned)
        foreach (var gift in gifts)
        {
            await uniqueCol.DeleteOneAsync(d => d.UniqueId == gift.UniqueId);
            await savedCol.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == gift.Slug);
        }

        // Update first gift with new crafted attributes
        // CRITICAL: Keep same UniqueId, Slug, Num - only attributes change!
        var now = DateTime.UtcNow.ToTimestamp();
        firstGift.Attributes = newAttrs;
        firstGift.Date = now;
        // From telelakel: "Gifts created through crafting can now be transferred immediately"
        firstGift.TransferLockedUntil = null;

        await uniqueCol.InsertOneAsync(firstGift);

        // Re-create saved gift entry
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            OwnerUserId = input.UserId,
            FromUserId = firstGift.FromUserId,
            GiftId = firstGift.GiftId,
            Stars = 0,
            IsUnique = true,
            UniqueSlug = firstGift.Slug,
            RandomId = firstGift.UniqueId,
            Saved = true,
            Date = now,
            GiftNum = firstGift.Num,
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