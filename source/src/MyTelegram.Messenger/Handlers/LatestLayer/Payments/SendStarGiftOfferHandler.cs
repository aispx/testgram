using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SendStarGiftOfferHandler(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendStarGiftOffer, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestSendStarGiftOffer obj)
    {
        var col = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var offerCol = mongoDatabase.GetCollection<StarGiftOfferDocument>("star-gift-offers");

        var doc = await col.Find(d => d.Slug == obj.Slug).FirstOrDefaultAsync();
        if (doc == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        // Check if gift was burned (used in crafting)
        if (doc!.Burned)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));

        if (doc.OwnerUserId == input.UserId) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var priceAmount = ((TStarsAmount)obj.Price).Amount;
        if (priceAmount <= 0) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Check offer_min_stars
        var offerMinStars = doc.OfferMinStars;
        if (offerMinStars > 0 && priceAmount < offerMinStars)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.User) RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        var recipientUserId = peer.PeerId;
        var expiresAt = DateTime.UtcNow.AddSeconds(obj.Duration).ToTimestamp();

        var offer = new StarGiftOfferDocument
        {
            SenderUserId = input.UserId,
            RecipientUserId = recipientUserId,
            Slug = obj.Slug,
            PriceAmount = priceAmount,
            ExpiresAt = expiresAt,
            RandomId = obj.RandomId,
            Resolved = false
        };
        await offerCol.InsertOneAsync(offer);

        var uniqueTl = UniqueStarGiftHelper.ToTl(doc);
        var action = new TMessageActionStarGiftPurchaseOffer
        {
            Gift = uniqueTl,
            Price = new TStarsAmount { Amount = priceAmount },
            ExpiresAt = expiresAt
        };

        var now = DateTime.UtcNow.ToTimestamp();
        Console.WriteLine($"[DEBUG] SendStarGiftOffer: Creating offer - Sender={input.UserId}, Recipient={recipientUserId}, Slug={obj.Slug}, RandomId={obj.RandomId}, Price={priceAmount}");
        
        await messageAppService.SendMessageAsync([new SendMessageInput(
            RequestInfo.Empty with { UserId = input.UserId, Layer = MyTelegramConsts.Layer, Date = now, RequestId = Guid.NewGuid() },
            input.UserId,
            new Peer(PeerType.User, recipientUserId),
            string.Empty,
            obj.RandomId,
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        )]);

        Console.WriteLine($"[DEBUG] SendStarGiftOffer: Message sent, RandomId={obj.RandomId}");
        return new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0 };
    }
}
