using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class TransferStarGiftHandler(IMongoDatabase mongoDatabase, IMessageAppService messageAppService, IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestTransferStarGift, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestTransferStarGift obj)
    {
        var col = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        UniqueStarGiftDocument? doc;
        bool ownerVerified = false;
        if (obj.Stargift is TInputSavedStarGiftUser u)
        {
            var saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsUnique && (d.MessageId == u.MsgId || d.RandomId == u.MsgId)).FirstOrDefaultAsync();
            doc = saved != null
                ? await col.Find(d => d.Slug == saved.UniqueSlug).FirstOrDefaultAsync()
                : null;
            ownerVerified = saved != null;
        }
        else
        {
            doc = obj.Stargift switch
            {
                TInputSavedStarGiftSlug s => await col.Find(d => d.Slug == s.Slug && d.OwnerUserId == input.UserId).FirstOrDefaultAsync(),
                TInputSavedStarGiftChat c => await col.Find(d => d.UniqueId == c.SavedId).FirstOrDefaultAsync(),
                _ => null
            };
        }

        if (doc == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
        if (!ownerVerified && doc!.OwnerUserId != input.UserId) RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

        // Check if gift was burned (used in crafting)
        if (doc!.Burned)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));

        // Check transfer cooldown (21 days after purchase/upgrade, or 7 days after new login)
        var currentTime = DateTime.UtcNow.ToTimestamp();
        if (doc!.TransferLockedUntil.HasValue && doc.TransferLockedUntil.Value > currentTime)
        {
            var secondsToWait = doc.TransferLockedUntil.Value - currentTime;
            throw new RpcException(new RpcError(400, $"STARGIFT_TRANSFER_TOO_EARLY_{secondsToWait}"));
        }

        var toPeer = peerHelper.GetPeer(obj.ToId, input.UserId)!;
        var newOwnerUserId = toPeer.PeerType == PeerType.User ? toPeer.PeerId : 0L;
        var newOwnerChannelId = toPeer.PeerType == PeerType.Channel ? toPeer.PeerId : 0L;

        // Update owner in unique-star-gifts
        await col.UpdateOneAsync(
            d => d.UniqueId == doc.UniqueId,
            Builders<UniqueStarGiftDocument>.Update
                .Set(d => d.OwnerUserId, newOwnerUserId)
                .Set(d => d.OwnerChannelId, newOwnerChannelId)
                .Set(d => d.ResellStars, 0L) // unlist from market on transfer
        );
        doc.OwnerUserId = newOwnerUserId;
        doc.OwnerChannelId = newOwnerChannelId;

        // Update saved-star-gifts
        await savedCol.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == doc.Slug);
        await savedCol.InsertOneAsync(new SavedStarGiftDocument
        {
            OwnerUserId = newOwnerUserId,
            OwnerChannelId = newOwnerChannelId,
            FromUserId = input.UserId,
            GiftId = doc.GiftId,
            Stars = 0,
            IsUnique = true,
            UniqueSlug = doc.Slug,
            RandomId = doc.UniqueId,
            Saved = true,
            Date = DateTime.UtcNow.ToTimestamp(),
            DocumentId = doc.DocumentId,
            DocumentAccessHash = doc.DocumentAccessHash,
            FileReference = doc.FileReference,
            DocumentDate = doc.DocumentDate,
            MimeType = doc.MimeType,
            DocumentSize = doc.DocumentSize,
            DcId = doc.DcId,
        });

        // Send service message to receiver
        var uniqueTl = UniqueStarGiftHelper.ToTl(doc);
        var action = new TMessageActionStarGiftUnique
        {
            Gift = uniqueTl,
            Transferred = true,
            Saved = true,
            FromId = new TPeerUser { UserId = input.UserId },
        };

        var now = DateTime.UtcNow.ToTimestamp();
        if (newOwnerUserId > 0)
        {
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = input.UserId, Layer = MyTelegramConsts.Layer, Date = now, RequestId = Guid.NewGuid() },
                input.UserId,
                new Peer(PeerType.User, newOwnerUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action
            )]);
        }

        return new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 };
    }
}
