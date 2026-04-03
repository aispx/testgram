using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

/// <summary>
/// Craft a new unique star gift from multiple existing gifts
/// From telelakel: "gifts that were on the blockchain can't be used in the first slot for crafting"
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.craftStarGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CraftStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPtsHelper ptsHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCraftStarGift, MyTelegram.Schema.IUpdates>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCraftStarGift obj)
    {
        if (obj.Stargift == null || obj.Stargift.Count < 2)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        // Load all gifts to craft
        var gifts = new List<(SavedStarGiftDocument? saved, UniqueStarGiftDocument? unique)>();
        foreach (var input_gift in obj.Stargift)
        {
            SavedStarGiftDocument? saved = null;
            UniqueStarGiftDocument? unique = null;

            if (input_gift is TInputSavedStarGiftUser u)
            {
                saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && (d.MessageId == u.MsgId || d.RandomId == u.MsgId)).FirstOrDefaultAsync();
                if (saved?.IsUnique == true)
                    unique = await uniqueCol.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftSlug s)
            {
                unique = await uniqueCol.Find(d => d.Slug == s.Slug && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
                if (unique != null)
                    saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == unique.Slug).FirstOrDefaultAsync();
            }
            else if (input_gift is TInputSavedStarGiftChat c)
            {
                unique = await uniqueCol.Find(d => d.UniqueId == c.SavedId && d.OwnerUserId == input.UserId).FirstOrDefaultAsync();
                if (unique != null)
                    saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == unique.Slug).FirstOrDefaultAsync();
            }

            if (saved == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
            gifts.Add((saved, unique));
        }

        // Validate first slot: can't be blockchain gift (from telelakel)
        var firstGift = gifts[0];
        if (firstGift.unique?.WasOnBlockchain == true)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Use first gift as base for crafted gift
        var baseGift = firstGift.saved!;
        var giftDoc = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == baseGift.GiftId).FirstOrDefaultAsync();
        if (giftDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Delete all source gifts
        foreach (var (saved, unique) in gifts)
        {
            await savedCol.DeleteOneAsync(d => d.Id == saved!.Id);
            if (unique != null)
                await uniqueCol.DeleteOneAsync(d => d.UniqueId == unique.UniqueId);
        }

        // Create new unique gift from crafting
        var uniqueId = await UniqueStarGiftHelper.NextUniqueIdAsync(mongoDatabase);
        var num = await UniqueStarGiftHelper.NextNumForGiftAsync(mongoDatabase, baseGift.GiftId);
        var slug = $"{giftDoc!.Title?.Replace(" ", "-").ToLower() ?? "gift"}-crafted-{num}";
        var attrs = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, giftDoc);
        var now = DateTime.UtcNow.ToTimestamp();

        var craftedDoc = new UniqueStarGiftDocument
        {
            UniqueId = uniqueId,
            GiftId = baseGift.GiftId,
            Title = giftDoc.Title ?? "Crafted Gift",
            Slug = slug,
            Num = num,
            OwnerUserId = input.UserId,
            FromUserId = input.UserId,
            Date = now,
            AvailabilityIssued = num,
            AvailabilityTotal = giftDoc.AvailabilityTotal ?? 0,
            NameHidden = false,
            InitialSaleStars = 0,
            OriginalRecipientUserId = input.UserId,
            Attributes = attrs,
            DocumentId = baseGift.DocumentId,
            DocumentAccessHash = baseGift.DocumentAccessHash,
            FileReference = baseGift.FileReference,
            DocumentDate = baseGift.DocumentDate,
            MimeType = baseGift.MimeType,
            DocumentSize = baseGift.DocumentSize,
            DcId = baseGift.DcId,
            // From telelakel: "Gifts created through crafting can now be transferred immediately"
            TransferLockedUntil = null,
        };
        await uniqueCol.InsertOneAsync(craftedDoc);

        // Create saved gift entry
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            OwnerUserId = input.UserId,
            FromUserId = input.UserId,
            GiftId = baseGift.GiftId,
            Stars = 0,
            IsUnique = true,
            UniqueSlug = slug,
            RandomId = uniqueId,
            Saved = true,
            Date = now,
            DocumentId = baseGift.DocumentId,
            DocumentAccessHash = baseGift.DocumentAccessHash,
            FileReference = baseGift.FileReference,
            DocumentDate = baseGift.DocumentDate,
            MimeType = baseGift.MimeType,
            DocumentSize = baseGift.DocumentSize,
            DcId = baseGift.DcId,
        });

        return new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 };
    }
}