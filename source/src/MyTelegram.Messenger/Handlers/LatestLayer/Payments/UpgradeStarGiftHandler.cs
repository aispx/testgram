using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class UpgradeStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPtsHelper ptsHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestUpgradeStarGift, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestUpgradeStarGift obj)
    {
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        SavedStarGiftDocument? saved = obj.Stargift switch
        {
            TInputSavedStarGiftUser u when u.MsgId == 0 =>
                await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UpgradeStars.HasValue && !d.IsUnique).FirstOrDefaultAsync(),
            TInputSavedStarGiftUser u =>
                await savedCol.Find(d => d.OwnerUserId == input.UserId && (d.MessageId == u.MsgId || d.MessageId == 0) && !d.IsUnique).FirstOrDefaultAsync(),
            TInputSavedStarGiftSlug s =>
                await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == s.Slug && !d.IsUnique).FirstOrDefaultAsync(),
            _ => null
        };

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
        if (!saved.PrepaidUpgrade && saved.UpgradeStars.HasValue && saved.UpgradeStars.Value > 0)
        {
            var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (balance < saved.UpgradeStars.Value)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value, gift: true, title: "Gift upgrade");
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

        return await UpgradeAsync(input, saved!, gift!);
    }

    public async Task<IUpdates> UpgradeAsync(IRequestInput input, SavedStarGiftDocument saved, StarGiftDocument? gift = null, long? overrideUserId = null)
    {
        var effectiveUserId = overrideUserId ?? input.UserId;
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
            FromUserId = saved.FromUserId,
            Date = currentTimestamp,
            AvailabilityIssued = num,
            AvailabilityTotal = gift.AvailabilityTotal ?? 0,
            NameHidden = saved.NameHidden,
            MessageText = saved.MessageText,
            InitialSaleStars = saved.Stars > 0 ? saved.Stars : (gift.Stars),
            OriginalRecipientUserId = effectiveUserId,
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

        // Update AvailabilityIssued for all unique gifts of this type
        await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .UpdateManyAsync(d => d.GiftId == saved.GiftId, Builders<UniqueStarGiftDocument>.Update.Set(d => d.AvailabilityIssued, num));

        // Replace saved-star-gift with unique version
        await savedCol.DeleteOneAsync(d => d.Id == saved!.Id);
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerUserId = effectiveUserId,
            FromUserId = saved.FromUserId,
            GiftId = saved.GiftId,
            Stars = saved.Stars,
            ConvertStars = 0,
            NameHidden = saved.NameHidden,
            Saved = true,
            Date = uniqueDoc.Date,
            MessageText = saved.MessageText,
            RandomId = uniqueDoc.UniqueId,
            IsUnique = true,
            UniqueSlug = uniqueDoc.Slug,
            GiftNum = saved.GiftNum,
            DocumentId = saved.DocumentId,
            DocumentAccessHash = saved.DocumentAccessHash,
            FileReference = saved.FileReference,
            DocumentDate = saved.DocumentDate,
            MimeType = saved.MimeType,
            DocumentSize = saved.DocumentSize,
            DcId = saved.DcId,
        });

        // Send messageActionStarGiftUnique service message
        var uniqueTl = UniqueStarGiftHelper.ToTl(uniqueDoc);
        var action = new TMessageActionStarGiftUnique
        {
            Gift = uniqueTl,
            Upgrade = true,
            Saved = true,
            FromId = saved.NameHidden ? null : new TPeerUser { UserId = saved.FromUserId },
        };

        // Send in dialog with gift sender (so client shows ActionUniqueGiftUpgradeOutbound with sender as un1)
        // If anonymous or self-gift, fall back to Saved Messages
        var dialogPeerId = saved.FromUserId > 0 && saved.FromUserId != effectiveUserId
            ? saved.FromUserId
            : effectiveUserId;

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            effectiveUserId,
            new Peer(PeerType.User, dialogPeerId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);

        var pts = await ptsHelper.IncrementPtsAsync(effectiveUserId, ptsHelper.GetCachedPts(effectiveUserId));
        var serviceMsg = new TMessageService
        {
            Id = pts,
            FromId = new TPeerUser { UserId = effectiveUserId },
            PeerId = new TPeerUser { UserId = dialogPeerId },
            Date = uniqueDoc.Date,
            Action = action,
            Out = true,
        };

        return new TUpdates
        {
            Updates = [new TUpdateNewMessage { Message = serviceMsg, Pts = pts, PtsCount = 1 }],
            Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = uniqueDoc.Date, Seq = 0
        };
    }
}
