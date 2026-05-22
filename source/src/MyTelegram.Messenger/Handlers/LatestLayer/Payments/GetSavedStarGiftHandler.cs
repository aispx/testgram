using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetSavedStarGiftHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<RequestGetSavedStarGift, ISavedStarGifts>
{
    protected override async Task<ISavedStarGifts> HandleCoreAsync(IRequestInput input, RequestGetSavedStarGift obj)
    {
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");

        var gifts = new TVector<ISavedStarGift>();

        foreach (var stargift in obj.Stargift)
        {
            SavedStarGiftDocument? doc = null;
            if (stargift is TInputSavedStarGiftUser u)
                doc = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.MessageId == u.MsgId).FirstOrDefaultAsync();
            else if (stargift is TInputSavedStarGiftChat c)
            {
                var channelId = (c.Peer as TInputPeerChannel)?.ChannelId ?? 0;
                var savedId = c.SavedId;
                doc = await savedCol.Find(d => d.OwnerChannelId == channelId && d.RandomId == savedId).FirstOrDefaultAsync();
            }
            else if (stargift is TInputSavedStarGiftSlug s)
                doc = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == s.Slug).FirstOrDefaultAsync();
            if (doc == null) continue;

            IStarGift giftTl;
            if (doc.IsUnique && doc.UniqueSlug != null)
            {
                var uniqueDoc = await uniqueCol.Find(d => d.Slug == doc.UniqueSlug).FirstOrDefaultAsync();
                giftTl = uniqueDoc != null ? UniqueStarGiftHelper.ToTl(uniqueDoc, documentId => CollectibleEmojiStatusHelper.DocumentExists(mongoDatabase, documentId)) : new TStarGift { Id = doc.GiftId, Stars = doc.Stars, ConvertStars = doc.ConvertStars };
            }
            else
            {
                var meta = await giftCol.Find(d => d.GiftId == doc.GiftId).FirstOrDefaultAsync();
                giftTl = new TStarGift
                {
                    Id = doc.GiftId,
                    Stars = doc.Stars,
                    ConvertStars = doc.ConvertStars,
                    UpgradeStars = doc.UpgradeStars,
                    Limited = meta?.Limited ?? false,
                    SoldOut = meta?.SoldOut ?? false,
                    AvailabilityRemains = meta?.AvailabilityRemains,
                    AvailabilityTotal = meta?.AvailabilityTotal,
                    Sticker = new TDocument
                    {
                        Id = doc.DocumentId,
                        AccessHash = doc.DocumentAccessHash,
                        FileReference = doc.FileReference,
                        Date = doc.DocumentDate,
                        MimeType = doc.MimeType,
                        Size = doc.DocumentSize,
                        DcId = doc.DcId,
                        Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
                    },
                };
            }

            IPeer? fromId = (!doc.NameHidden && doc.FromUserId != 0) ? new TPeerUser { UserId = doc.FromUserId } : null;
            // Item 14: restore preserved entities (custom emoji, bold, etc.) instead of stripping.
            ITextWithEntities? message = !string.IsNullOrEmpty(doc.MessageText)
                ? new TTextWithEntities { Text = doc.MessageText, Entities = doc.MessageEntities ?? new TVector<IMessageEntity>() } : null;

            gifts.Add(new TSavedStarGift
            {
                Gift = giftTl,
                FromId = fromId,
                Date = doc.Date,
                Message = message,
                MsgId = (!doc.IsUnique && doc.MessageId > 0) ? doc.MessageId : null,
                SavedId = (doc.IsUnique || doc.MessageId == 0) ? doc.RandomId : null,
                ConvertStars = doc.IsUnique || doc.IsAuction ? null : (doc.ConvertStars > 0 ? doc.ConvertStars : null),
                UpgradeStars = !doc.IsUnique && doc.PrepaidUpgrade ? doc.UpgradeStars : null,
                CanUpgrade = !doc.IsUnique && doc.UpgradeStars.HasValue,
                Unsaved = !doc.Saved,
                NameHidden = doc.NameHidden,
                GiftNum = doc.GiftNum,
            });
        }

        return new TSavedStarGifts { Count = gifts.Count, Gifts = gifts, NextOffset = null, Chats = new TVector<IChat>(), Users = new TVector<IUser>() };
    }
}
