using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class ResolveStarGiftOfferHandler(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestResolveStarGiftOffer, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestResolveStarGiftOffer obj)
    {
        var offerCol = mongoDatabase.GetCollection<StarGiftOfferDocument>("star-gift-offers");
        var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");

        // Try to find by MessageId first, then by RandomId
        Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Looking for offer - OfferMsgId={obj.OfferMsgId}, UserId={input.UserId}");
        
        var now = DateTime.UtcNow.ToTimestamp();
        
        // SECURITY FIX: Check expiration before processing
        var offer = await offerCol.Find(o => 
            o.MessageId == obj.OfferMsgId && 
            o.RecipientUserId == input.UserId && 
            !o.Resolved &&
            o.ExpiresAt > now
        ).FirstOrDefaultAsync();
        
        if (offer == null)
        {
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Not found by MessageId={obj.OfferMsgId} or expired");
            // Don't fallback to RandomId to prevent IDOR
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }
        
        Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Found offer - Slug={offer.Slug}, MessageId={offer.MessageId}, Price={offer.PriceAmount}");

        var doc = await uniqueCol.Find(d => d.Slug == offer.Slug).FirstOrDefaultAsync();
        if (doc == null) RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();

        // Check if gift was burned (used in crafting)
        if (doc!.Burned)
            throw new RpcException(new RpcError(400, "STARGIFT_ALREADY_BURNED"));

        // SECURITY FIX: Atomic update to prevent race conditions
        // Only mark as resolved if still unresolved (prevents double-processing)
        var updateResult = await offerCol.UpdateOneAsync(
            o => o.Id == offer.Id && !o.Resolved,
            Builders<StarGiftOfferDocument>.Update.Set(o => o.Resolved, true)
        );
        
        if (updateResult.ModifiedCount == 0)
        {
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Offer already resolved (race condition prevention)");
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (obj.Decline)
        {
            // Send declined message to BUYER - tells them the offer was declined
            var declinedAction = new TMessageActionStarGiftPurchaseOffer
            {
                Declined = true,
                Gift = UniqueStarGiftHelper.ToTl(doc),
                Price = new TStarsAmount { Amount = offer.PriceAmount },
                ExpiresAt = offer.ExpiresAt
            };
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = offer.RecipientUserId, Layer = MyTelegramConsts.Layer, Date = now, RequestId = Guid.NewGuid() },
                offer.RecipientUserId, // From the seller
                new Peer(PeerType.User, offer.SenderUserId), // To the buyer
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: declinedAction
            )]);
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Sent declined message to buyer {offer.SenderUserId}");
        }
        else
        {
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Processing acceptance - Slug={doc.Slug}, Buyer={offer.SenderUserId}, Price={offer.PriceAmount}");
            
            // SECURITY FIX: Check buyer has sufficient balance before any operations
            var buyerBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, offer.SenderUserId);
            if (buyerBalance < offer.PriceAmount)
            {
                Console.WriteLine($"[SECURITY] ResolveStarGiftOffer: Insufficient balance. Has={buyerBalance}, Need={offer.PriceAmount}");
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            }
            
            // SECURITY FIX: Validate price is reasonable (prevent integer overflow/malicious values)
            if (offer.PriceAmount <= 0 || offer.PriceAmount > long.MaxValue / 2)
            {
                Console.WriteLine($"[SECURITY] ResolveStarGiftOffer: Invalid price amount: {offer.PriceAmount}");
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            }
            
            // SECURITY FIX: Atomic transfer - update NFT ownership only if current owner is the seller
            var nftUpdateResult = await uniqueCol.UpdateOneAsync(
                d => d.UniqueId == doc.UniqueId && d.OwnerUserId == offer.RecipientUserId,
                Builders<UniqueStarGiftDocument>.Update
                    .Set(d => d.OwnerUserId, offer.SenderUserId)
                    .Set(d => d.OwnerChannelId, 0L)
            );
            
            if (nftUpdateResult.ModifiedCount == 0)
            {
                Console.WriteLine($"[SECURITY] ResolveStarGiftOffer: NFT ownership changed or already transferred");
                RpcErrors.RpcErrors400.StargiftNotFound.ThrowRpcError();
            }
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: NFT owner changed to {offer.SenderUserId}");

            // Delete any existing saved gift for this slug and create new one for buyer
            await savedCol.DeleteOneAsync(s => s.IsUnique && s.UniqueSlug == doc.Slug);
            await savedCol.InsertOneAsync(new SavedStarGiftDocument
            {
                OwnerUserId = offer.SenderUserId, // Buyer gets the saved gift
                OwnerChannelId = 0L,
                FromUserId = offer.RecipientUserId, // Original owner
                GiftId = doc.GiftId,
                Stars = 0,
                IsUnique = true,
                UniqueSlug = doc.Slug,
                RandomId = doc.UniqueId,
                Saved = true,
                Date = now,
                DocumentId = doc.DocumentId,
                DocumentAccessHash = doc.DocumentAccessHash,
                FileReference = doc.FileReference,
                DocumentDate = doc.DocumentDate,
                MimeType = doc.MimeType,
                DocumentSize = doc.DocumentSize,
                DcId = doc.DcId
            });
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Saved gift created for buyer {offer.SenderUserId}");

            // Update balances and add transactions for BUYER (pays for the NFT)
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, offer.SenderUserId, -offer.PriceAmount);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, offer.SenderUserId, -offer.PriceAmount, 
                offer: true, peerUserId: offer.RecipientUserId, stargiftSlug: doc.Slug);
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Debited buyer {offer.SenderUserId}: -{offer.PriceAmount}");

            // Update balances and add transactions for SELLER (receives payment for the NFT)
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, offer.RecipientUserId, offer.PriceAmount);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, offer.RecipientUserId, offer.PriceAmount, 
                offer: true, peerUserId: offer.SenderUserId, stargiftSlug: doc.Slug);
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Credited seller {offer.RecipientUserId}: +{offer.PriceAmount}");

            // Send accepted message to SELLER (the one who owned the NFT) - tells them they sold it
            // Message from the BUYER to the SELLER
            var acceptedAction = new TMessageActionStarGiftPurchaseOffer
            {
                Accepted = true,
                Gift = UniqueStarGiftHelper.ToTl(doc),
                Price = new TStarsAmount { Amount = offer.PriceAmount },
                ExpiresAt = offer.ExpiresAt
            };
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = offer.SenderUserId, Layer = MyTelegramConsts.Layer, Date = now, RequestId = Guid.NewGuid() },
                offer.SenderUserId, // From the buyer
                new Peer(PeerType.User, offer.RecipientUserId), // To the seller
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: acceptedAction
            )]);
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Sent accepted message to seller {offer.RecipientUserId}");

            // Send from_offer message to BUYER (the one who sent the offer) - tells them they received the NFT
            // Message from the SELLER to the BUYER
            var receivedAction = new TMessageActionStarGiftUnique
            {
                FromOffer = true,
                FromId = new TPeerUser { UserId = offer.RecipientUserId }, // From the original owner/seller
                Gift = UniqueStarGiftHelper.ToTl(doc)
            };
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = offer.RecipientUserId, Layer = MyTelegramConsts.Layer, Date = now, RequestId = Guid.NewGuid() },
                offer.RecipientUserId, // From the seller
                new Peer(PeerType.User, offer.SenderUserId), // To the buyer
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: receivedAction
            )]);
            Console.WriteLine($"[DEBUG] ResolveStarGiftOffer: Sent from_offer message to buyer {offer.SenderUserId}");
        }

        return new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0 };
    }
}
