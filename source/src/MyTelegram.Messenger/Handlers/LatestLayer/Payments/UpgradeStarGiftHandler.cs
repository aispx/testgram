using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class UpgradeStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpgradeStarGift, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpgradeStarGift obj)
    {
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        // Resolve the saved gift precisely from whichever InputSavedStarGift
        // variant the client sent. Loose matching ("MessageId == X || MessageId == 0")
        // used to silently grab the oldest non-unique gift in the user's bag,
        // so a click on Klitor would actually upgrade a Blue Star.
        SavedStarGiftDocument? saved = null;
        switch (obj.Stargift)
        {
            case TInputSavedStarGiftUser u when u.MsgId != 0:
                saved = await savedCol.Find(d =>
                    d.OwnerUserId == input.UserId && d.MessageId == u.MsgId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                if (saved == null)
                {
                    saved = await savedCol.Find(d =>
                        d.OwnerUserId == input.UserId && d.RandomId == u.MsgId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                }
                if (saved == null)
                {
                    saved = await savedCol
                        .Find(d => d.OwnerUserId == input.UserId && d.MessageId == 0 && !d.IsUnique && d.UpgradeStars.HasValue)
                        .Sort(Builders<SavedStarGiftDocument>.Sort.Descending(d => d.Date))
                        .FirstOrDefaultAsync();
                }
                break;
            case TInputSavedStarGiftUser:
                // msg_id == 0 → most recently received upgradable gift only.
                saved = await savedCol
                    .Find(d => d.OwnerUserId == input.UserId && d.MessageId == 0 && !d.IsUnique && d.UpgradeStars.HasValue)
                    .Sort(Builders<SavedStarGiftDocument>.Sort.Descending(d => d.Date))
                    .FirstOrDefaultAsync();
                break;
            case TInputSavedStarGiftChat c:
                var channelId = (c.Peer as TInputPeerChannel)?.ChannelId ?? 0;
                saved = await savedCol.Find(d =>
                    d.OwnerChannelId == channelId && d.RandomId == c.SavedId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                break;
            case TInputSavedStarGiftSlug s:
                saved = await savedCol.Find(d =>
                    d.OwnerUserId == input.UserId && d.UniqueSlug == s.Slug && !d.IsUnique).FirstOrDefaultAsync();
                break;
        }

        if (saved == null)
            throw new RpcException(new RpcError(400, "STARGIFT_NOT_FOUND"));

        if (saved!.IsUnique)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_UPGRADED"));

        // Check if upgrade is available
        if (!saved.UpgradeStars.HasValue || saved.UpgradeStars.Value <= 0)
            throw new RpcException(new RpcError(400, "STARGIFT_UPGRADE_UNAVAILABLE"));

        // Check if this is a unique gift that was burned
        if (saved.IsUnique && !string.IsNullOrEmpty(saved.UniqueSlug))
        {
            var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
            var uniqueDoc = await uniqueCol.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync();
            if (uniqueDoc?.Burned == true)
                throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));
        }

        // Deduct upgrade stars only if not prepaid
        long? chargedUpgradeStars = null;
        if (!saved.PrepaidUpgrade && saved.UpgradeStars.HasValue && saved.UpgradeStars.Value > 0)
        {
            var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (balance < saved.UpgradeStars.Value)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value);
            chargedUpgradeStars = saved.UpgradeStars.Value;
        }

        // upgradeStarGift is only for prepaid upgrades (sender already paid)
        // For non-prepaid, client uses getPaymentForm flow instead
        var gift = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == saved!.GiftId).FirstOrDefaultAsync();
        if (gift == null)
        {
            // Try to find gift by looking up in star-gift-auction-acquired for auction gifts
            if (saved.IsAuction && saved.GiftNum.HasValue)
            {
                var acquiredGift = await mongoDatabase.GetCollection<BsonDocument>("star-gift-auction-acquired")
                    .Find(d => d.GetElement("UserId").Value == saved.OwnerUserId && d.GetElement("GiftNum").Value == saved.GiftNum)
                    .FirstOrDefaultAsync();
                if (acquiredGift != null)
                {
                    var actualGiftId = acquiredGift.GetElement("GiftId").Value.AsInt64;
                    gift = await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
                        .Find(d => d.GiftId == actualGiftId).FirstOrDefaultAsync();
                    if (gift != null)
                    {
                        // Update saved gift with correct GiftId
                        await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                            .UpdateOneAsync(d => d.Id == saved.Id, Builders<SavedStarGiftDocument>.Update.Set(d => d.GiftId, actualGiftId));
                    }
                }
            }
        }
        if (gift == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        return await UpgradeAsync(input, saved!, gift!, chargedUpgradeStars, "Gift upgrade");
    }

    public async Task<IUpdates> UpgradeAsync(IRequestInput input, SavedStarGiftDocument saved, StarGiftDocument? gift = null,
        long? chargedUpgradeStars = null, string? transactionTitle = null, long? overrideUserId = null)
    {
        var effectiveUserId = saved.OwnerChannelId > 0 ? 0 : (overrideUserId ?? saved.OwnerUserId);
        if (effectiveUserId == 0 && saved.OwnerChannelId == 0)
        {
            effectiveUserId = input.UserId;
        }
        var effectiveChannelId = saved.OwnerChannelId;
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        gift ??= await mongoDatabase.GetCollection<StarGiftDocument>("star-gifts")
            .Find(d => d.GiftId == saved.GiftId).FirstOrDefaultAsync();
        if (gift == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        var uniqueId = await UniqueStarGiftHelper.NextUniqueIdAsync(mongoDatabase);
        
        // For auction gifts, use the GiftNum assigned during auction instead of incrementing counter
        int num = saved.GiftNum > 0 ? saved.GiftNum.Value : await UniqueStarGiftHelper.NextNumForGiftAsync(mongoDatabase, saved.GiftId);
        
        var baseTitle = gift!.Title ?? "Gift";
        var slug = $"{baseTitle.Replace(" ", "-").ToLower()}-{num}";
        var attrs = await UniqueStarGiftHelper.GenerateAttributesAsync(mongoDatabase, gift!);
        var title = baseTitle;

        var currentTimestamp = DateTime.UtcNow.ToTimestamp();

        var uniqueDoc = new UniqueStarGiftDocument
        {
            Id = ObjectId.GenerateNewId(),
            UniqueId = uniqueId,
            GiftId = saved.GiftId,
            Title = title,
            Slug = slug,
            Num = num,
            OwnerUserId = effectiveUserId,
            OwnerChannelId = effectiveChannelId,
            FromUserId = saved.FromUserId,
            Date = currentTimestamp,
            AvailabilityIssued = num,
            AvailabilityTotal = gift.AvailabilityTotal ?? 0,
            NameHidden = saved.NameHidden,
            MessageText = saved.MessageText,
            MessageEntities = saved.MessageEntities,
            InitialSaleStars = saved.Stars > 0 ? saved.Stars : (gift.Stars),
            OriginalRecipientUserId = effectiveChannelId > 0 ? 0 : effectiveUserId,
            Attributes = attrs,
            DocumentId = saved.DocumentId,
            DocumentAccessHash = saved.DocumentAccessHash,
            FileReference = saved.FileReference,
            DocumentDate = saved.DocumentDate,
            MimeType = saved.MimeType,
            DocumentSize = saved.DocumentSize,
            DcId = saved.DcId,
            // Set 21-day transfer cooldown after upgrade (from telelakel: "21-day transfer cooldown")
            TransferLockedUntil = currentTimestamp + (21 * 24 * 60 * 60),
        };
        await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts").InsertOneAsync(uniqueDoc);

        if (chargedUpgradeStars.HasValue && chargedUpgradeStars.Value > 0)
        {
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, effectiveUserId, -chargedUpgradeStars.Value,
                gift: true, title: transactionTitle ?? "Gift upgrade", stargiftUpgrade: true, stargiftSlug: uniqueDoc.Slug);
        }

        // Update AvailabilityIssued for all unique gifts of this type
        await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .UpdateManyAsync(d => d.GiftId == saved.GiftId, Builders<UniqueStarGiftDocument>.Update.Set(d => d.AvailabilityIssued, num));

        // Replace saved-star-gift with unique version
        await savedCol.DeleteOneAsync(d => d.Id == saved!.Id);
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerUserId = effectiveUserId,
            OwnerChannelId = effectiveChannelId,
            FromUserId = saved.FromUserId,
            GiftId = saved.GiftId,
            Stars = saved.Stars,
            ConvertStars = 0,
            NameHidden = saved.NameHidden,
            Saved = true,
            Date = uniqueDoc.Date,
            MessageText = saved.MessageText,
            MessageEntities = saved.MessageEntities,
            RandomId = uniqueDoc.UniqueId,
            IsUnique = true,
            UniqueSlug = uniqueDoc.Slug,
            GiftNum = saved.GiftNum,
            CanTransferAt = uniqueDoc.TransferLockedUntil,
            CanResellAt = uniqueDoc.TransferLockedUntil,
            CanCraftAt = uniqueDoc.TransferLockedUntil,
            DocumentId = saved.DocumentId,
            DocumentAccessHash = saved.DocumentAccessHash,
            FileReference = saved.FileReference,
            DocumentDate = saved.DocumentDate,
            MimeType = saved.MimeType,
            DocumentSize = saved.DocumentSize,
            DcId = saved.DcId,
        });

        // Telegram does not need a visible upgrade notice for channel-owned gifts:
        // keep the collectible on the channel profile without injecting a service
        // message into either the channel history or the admin's Saved Messages.
        if (effectiveChannelId == 0)
        {
            var uniqueTl = UniqueStarGiftHelper.ToTl(uniqueDoc);
            var action = new TMessageActionStarGiftUnique
            {
                Gift = uniqueTl,
                Upgrade = true,
                Saved = true,
                FromId = saved.NameHidden ? null : new TPeerUser { UserId = saved.FromUserId },
            };

            var sendInput = new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                effectiveUserId,
                new Peer(PeerType.User, effectiveUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action
            );
            await messageAppService.SendMessageAsync([sendInput]);
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = uniqueDoc.Date, Seq = 0
        };
    }
}
