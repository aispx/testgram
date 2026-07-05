using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Possible errors
/// Code Type Description
/// 400 STARGIFT_INVALID The passed gift is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.getCraftStarGifts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetCraftStarGiftsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetCraftStarGifts, MyTelegram.Schema.Payments.ISavedStarGifts>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.ISavedStarGifts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetCraftStarGifts obj)
    {
        if (obj.GiftId == 0)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        var uniqueFilter =
            Builders<UniqueStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId) &
            Builders<UniqueStarGiftDocument>.Filter.Eq(d => d.GiftId, obj.GiftId) &
            Builders<UniqueStarGiftDocument>.Filter.Ne(d => d.Burned, true) &
            Builders<UniqueStarGiftDocument>.Filter.Ne(d => d.Crafted, true);

        var total = (int)await uniqueCol.CountDocumentsAsync(uniqueFilter);
        var skip = int.TryParse(obj.Offset, out var parsedOffset) ? parsedOffset : 0;
        var limit = obj.Limit > 0 ? obj.Limit : 100;

        var uniqueDocs = await uniqueCol.Find(uniqueFilter)
            .Sort(Builders<UniqueStarGiftDocument>.Sort.Descending(d => d.Date))
            .Skip(skip)
            .Limit(limit)
            .ToListAsync();

        var slugs = uniqueDocs.Select(d => d.Slug).ToList();
        var savedBySlug = slugs.Count > 0
            ? (await savedCol.Find(
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.OwnerUserId, input.UserId) &
                    Builders<SavedStarGiftDocument>.Filter.Eq(d => d.IsUnique, true) &
                    Builders<SavedStarGiftDocument>.Filter.In(d => d.UniqueSlug, slugs))
                .ToListAsync())
                .Where(d => d.UniqueSlug != null)
                .ToDictionary(d => d.UniqueSlug!)
            : new Dictionary<string, SavedStarGiftDocument>();

        var gifts = new TVector<ISavedStarGift>();
        var nowForCooldown = DateTime.UtcNow.ToTimestamp();
        foreach (var uniqueDoc in uniqueDocs)
        {
            savedBySlug.TryGetValue(uniqueDoc.Slug, out var saved);
            var cooldownAt = uniqueDoc.TransferLockedUntil;
            if (cooldownAt.HasValue && cooldownAt.Value <= nowForCooldown) cooldownAt = null;
            var canTransferAt = LaterTimestamp(saved?.CanTransferAt, cooldownAt);
            if (canTransferAt.HasValue && canTransferAt.Value <= nowForCooldown) canTransferAt = null;
            var canResellAt = LaterTimestamp(saved?.CanResellAt, cooldownAt);
            if (canResellAt.HasValue && canResellAt.Value <= nowForCooldown) canResellAt = null;
            var canCraftAt = LaterTimestamp(saved?.CanCraftAt, cooldownAt);
            if (canCraftAt.HasValue && canCraftAt.Value <= nowForCooldown) canCraftAt = null;

            gifts.Add(new TSavedStarGift
            {
                Gift = UniqueStarGiftHelper.ToTl(
                    uniqueDoc,
                    documentId => CollectibleEmojiStatusHelper.DocumentExists(mongoDatabase, documentId)),
                FromId = uniqueDoc.FromUserId != 0 && saved?.NameHidden != true ? new TPeerUser { UserId = uniqueDoc.FromUserId } : null,
                Date = saved?.Date ?? uniqueDoc.Date,
                Message = !string.IsNullOrEmpty(saved?.MessageText)
                    ? new TTextWithEntities
                    {
                        Text = saved.MessageText,
                        Entities = saved.MessageEntities ?? new TVector<IMessageEntity>(),
                    }
                    : null,
                MsgId = saved is { MessageId: > 0 } ? saved.MessageId : null,
                SavedId = uniqueDoc.UniqueId,
                Unsaved = saved?.Saved == false,
                NameHidden = saved?.NameHidden ?? uniqueDoc.NameHidden,
                PinnedToTop = saved?.PinnedToTop ?? false,
                GiftNum = saved?.GiftNum ?? uniqueDoc.Num,
                CanTransferAt = canTransferAt,
                CanResellAt = canResellAt,
                CanCraftAt = canCraftAt,
            });
        }

        return new TSavedStarGifts
        {
            Count = total,
            Gifts = gifts,
            NextOffset = skip + uniqueDocs.Count < total ? (skip + uniqueDocs.Count).ToString() : null,
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
        };
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
