using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Messenger.Services.Boosts;
using MyTelegram.Schema.Payments;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SendStarsFormHandler(
    IMongoDatabase mongoDatabase,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IServiceProvider services,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<RequestSendStarsForm, IPaymentResult>
{
    protected override async Task<IPaymentResult> HandleCoreAsync(IRequestInput input, RequestSendStarsForm obj)
    {
        // Handle Premium purchase with Stars
        if (obj.Invoice is TInputInvoicePremiumGiftStars premiumStars)
        {
            // Get Stars price based on months (matching PremiumPromoConverter)
            long starsPrice = premiumStars.Months switch
            {
                1 => 2500,   // 1 month = 2500 stars
                3 => 7000,   // 3 months = 7000 stars
                6 => 13000,  // 6 months = 13000 stars
                12 => 25000, // 12 months = 25000 stars
                _ => 0
            };

            if (starsPrice == 0)
                RpcErrors.RpcErrors400.BotInvoiceInvalid.ThrowRpcError();

            // Check Stars balance
            var premiumBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (premiumBalance < starsPrice)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            // Get target user
            var targetUserId = premiumStars.UserId switch
            {
                TInputUserSelf => input.UserId,
                TInputUser u => u.UserId,
                _ => peerHelper.GetPeer(premiumStars.UserId, input.UserId).PeerId
            };

            // Deduct Stars from sender
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -starsPrice);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -starsPrice,
                title: $"Premium gift ({premiumStars.Months} months)", peerUserId: targetUserId, premiumGiftMonths: premiumStars.Months);

            // Activate Premium subscription for target user
            await ActivatePremiumAsync(targetUserId, premiumStars.Months);

            // Create boost slots for sender (boosts_per_sent_gift = 3)
            // Sender gets 3 boost slots when gifting Premium to another user
            if (targetUserId != input.UserId)
            {
                var boostSlotCol = mongoDatabase.GetCollection<UserBoostSlotDocument>("user-boost-slots");

                // Find next available slot numbers for sender
                var existingSlots = await boostSlotCol.Find(x => x.UserId == input.UserId)
                    .SortByDescending(x => x.Slot)
                    .Limit(1)
                    .ToListAsync();

                int nextSlot = existingSlots.Count > 0 ? existingSlots[0].Slot + 1 : 0;

                // Create 3 new boost slots for sender
                for (int i = 0; i < 3; i++)
                {
                    await boostSlotCol.InsertOneAsync(new UserBoostSlotDocument
                    {
                        Id = $"boost-slot-{input.UserId}-{nextSlot + i}",
                        UserId = input.UserId,
                        Slot = nextSlot + i,
                        Cooldown = 0,
                        ChannelId = null
                    });
                }
            }

            // Create 1 boost slot for recipient
            {
                var boostSlotCol = mongoDatabase.GetCollection<UserBoostSlotDocument>("user-boost-slots");

                var existingSlots = await boostSlotCol.Find(x => x.UserId == targetUserId)
                    .SortByDescending(x => x.Slot)
                    .Limit(1)
                    .ToListAsync();

                int nextSlot = existingSlots.Count > 0 ? existingSlots[0].Slot + 1 : 0;

                await boostSlotCol.InsertOneAsync(new UserBoostSlotDocument
                {
                    Id = $"boost-slot-{targetUserId}-{nextSlot}",
                    UserId = targetUserId,
                    Slot = nextSlot,
                    Cooldown = 0,
                    ChannelId = null
                });
            }

            // Send gift message to recipient
            var action = new TMessageActionGiftPremium
            {
                Currency = "XTR",
                Amount = starsPrice,
                Days = premiumStars.Months * 30,
                Message = premiumStars.Message,
            };

            await messageAppService.SendMessageAsync([new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                new Peer(PeerType.User, targetUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: action
            )]);

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
        {
            var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
            var auction = await auctionCol.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
            if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var auctionBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            var existingBids = auction.Bids.Where(b => b.UserId == input.UserId && !b.Returned).ToList();
            var existingBid = existingBids.OrderByDescending(b => b.Amount).FirstOrDefault();
            long existingTotal = existingBids.Sum(b => b.Amount);
            long extraNeeded = existingTotal > 0 ? auctionBid.BidAmount - existingTotal : auctionBid.BidAmount;
            if (auctionBalance < extraNeeded) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            if (existingBids.Count > 0)
            {
                foreach (var b in existingBids) b.Returned = true;
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -extraNeeded);
            }
            else
            {
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount);
            }

            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -extraNeeded,
                title: $"Auction bid for gift {auctionBid.GiftId}", stargiftAuctionBid: true);

            // Compute top BEFORE adding new bid (to detect who gets pushed out)
            var previousTop = auction.Bids
                .Where(b => !b.Returned && b.UserId != input.UserId)
                .GroupBy(b => b.UserId)
                .Select(g => g.OrderByDescending(b => b.Amount).First())
                .OrderByDescending(b => b.Amount)
                .ToList();

            auction.Bids.Add(new AuctionBid
            {
                UserId = input.UserId,
                Amount = auctionBid.BidAmount,
                Date = DateTime.UtcNow.ToTimestamp(),
                NameHidden = auctionBid.HideName,
                MessageText = (auctionBid.Message as TTextWithEntities)?.Text,
            });
            auction.Version++;
            await auctionCol.ReplaceOneAsync(x => x.Id == auction.Id, auction);

            // Compute new top after adding bid
            var newTopIds = auction.Bids
                .Where(b => !b.Returned)
                .GroupBy(b => b.UserId)
                .Select(g => g.OrderByDescending(b => b.Amount).First())
                .OrderByDescending(b => b.Amount)
                .Take(auction.GiftsPerRound)
                .Select(b => b.UserId)
                .ToHashSet();

            // Notify users who were in previous top but now pushed out
            for (int i = 0; i < previousTop.Count; i++)
            {
                var bid = previousTop[i];
                if (newTopIds.Contains(bid.UserId)) continue;

                // How many spots are available (giftsPerRound)
                var n = auction.GiftsPerRound;
                var msg = $"Your bid on gift 🎁 is no longer in the top {n} highest. Increase your bid to keep competing for the gift.";
                await messageAppService.SendMessageAsync([new SendMessageInput(
                    RequestInfo.Empty with
                    {
                        UserId = MyTelegramConsts.NotificationServiceUserId,
                        Layer = MyTelegramConsts.Layer,
                        Date = DateTime.UtcNow.ToTimestamp(),
                        RequestId = Guid.NewGuid()
                    },
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, bid.UserId),
                    msg,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.Text,
                    messageType: MessageType.Text
                )]);
            }

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftPrepaidUpgrade prepaidInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            var peer = peerHelper.GetPeer(prepaidInvoice.Peer, input.UserId)!;
            var saved = await savedCol.Find(d =>
                d.OwnerUserId == peer.PeerId &&
                d.PrepaidUpgradeHash == prepaidInvoice.Hash &&
                !d.IsUnique).FirstOrDefaultAsync();
            if (saved == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var upgradeHandler = new UpgradeStarGiftHandler(mongoDatabase, messageAppService, null!);
            return new TPaymentResult { Updates = await upgradeHandler.UpgradeAsync(input, saved!, overrideUserId: saved!.OwnerUserId) };
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved = null;
            if (upgradeInvoice.Stargift is TInputSavedStarGiftUser u)
            {
                // First try auction gifts (they have proper UpgradeStars and GiftNum)
                saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.IsAuction && !d.IsUnique && d.UpgradeStars.HasValue && d.GiftNum.HasValue).FirstOrDefaultAsync();
                // Then try exact message match
                if (saved == null)
                {
                    saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.MessageId == u.MsgId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                }
                // Finally try MessageId = 0 (for older saved gifts)
                if (saved == null)
                {
                    saved = await savedCol.Find(d => d.OwnerUserId == input.UserId && d.MessageId == 0 && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync();
                }
            }
            if (saved == null || saved.IsUnique || !saved.UpgradeStars.HasValue)
                RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

            var bal = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (bal < saved!.UpgradeStars!.Value)
                RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -saved.UpgradeStars.Value, gift: true, title: "In-App Purchase", stargiftUpgrade: true);

            var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
            StarGiftDocument? giftDoc = null;
            
            Console.WriteLine($"[DEBUG] Upgrade: saved.IsAuction={saved.IsAuction}, GiftId={saved.GiftId}, GiftNum={saved.GiftNum}");
            
            // First try to find via auction lookup
            if (saved.IsAuction && saved.GiftNum.HasValue && saved.GiftNum.Value > 0)
            {
                try
                {
                    var acquiredCollection = mongoDatabase.GetCollection<BsonDocument>("star-gift-auction-acquired");
                    var filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("UserId", saved.OwnerUserId),
                        Builders<BsonDocument>.Filter.Eq("GiftNum", saved.GiftNum.Value)
                    );
                    var acquiredGift = await acquiredCollection.Find(filter).FirstOrDefaultAsync();
                    Console.WriteLine($"[DEBUG] Acquired gift: {acquiredGift != null}");
                    if (acquiredGift != null)
                    {
                        var actualGiftId = acquiredGift.GetValue("GiftId").AsInt64;
                        Console.WriteLine($"[DEBUG] Looking up star-gifts with GiftId={actualGiftId}");
                        var giftFilter = Builders<StarGiftDocument>.Filter.Eq(g => g.GiftId, actualGiftId);
                        giftDoc = await giftCol.Find(giftFilter).FirstOrDefaultAsync();
                        Console.WriteLine($"[DEBUG] Found gift in star-gifts: {giftDoc != null}");
                        if (giftDoc != null)
                        {
                            await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                                .UpdateOneAsync(d => d.Id == saved.Id, Builders<SavedStarGiftDocument>.Update.Set(d => d.GiftId, actualGiftId));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Auction lookup error: {ex.Message}");
                }
            }
            
            // Fallback to direct lookup
            if (giftDoc == null)
            {
                Console.WriteLine($"[DEBUG] Falling back to direct lookup with GiftId={saved.GiftId}");
                var directFilter = Builders<StarGiftDocument>.Filter.Eq(g => g.GiftId, saved.GiftId);
                giftDoc = await giftCol.Find(directFilter).FirstOrDefaultAsync();
                Console.WriteLine($"[DEBUG] Direct lookup result: {giftDoc != null}");
            }
            
            if (giftDoc == null)
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            // Get IPtsHelper from service provider
            var ptsHelper = services.GetRequiredService<IPtsHelper>();
            var upgradeHandler = new UpgradeStarGiftHandler(mongoDatabase, messageAppService, ptsHelper);
            return new TPaymentResult { Updates = await upgradeHandler.UpgradeAsync(input, saved, giftDoc) };
        }

        if (obj.Invoice is TInputInvoiceStarGiftDropOriginalDetails dropInvoice)
        {
            const long dropCost = 25;
            var savedCol2 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved2 = dropInvoice.Stargift switch
            {
                TInputSavedStarGiftUser u2 => await savedCol2.Find(d => d.OwnerUserId == input.UserId && (d.MessageId == u2.MsgId || d.RandomId == u2.MsgId)).FirstOrDefaultAsync(),
                TInputSavedStarGiftSlug s2 => await savedCol2.Find(d => d.OwnerUserId == input.UserId && d.UniqueSlug == s2.Slug).FirstOrDefaultAsync(),
                TInputSavedStarGiftChat c2 => await savedCol2.Find(d => d.OwnerUserId == input.UserId && d.RandomId == c2.SavedId).FirstOrDefaultAsync(),
                _ => null
            };
            if (saved2 == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            var bal2 = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (bal2 < dropCost) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            await savedCol2.UpdateOneAsync(
                Builders<SavedStarGiftDocument>.Filter.Eq(d => d.Id, saved2!.Id),
                Builders<SavedStarGiftDocument>.Update
                    .Set(d => d.MessageText, null)
                    .Set(d => d.FromUserId, 0)
                    .Set(d => d.NameHidden, true));

            // For unique gifts also update unique-star-gifts
            if (saved2!.IsUnique && saved2.UniqueSlug != null)
            {
                await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                    .UpdateOneAsync(d => d.Slug == saved2.UniqueSlug,
                        Builders<UniqueStarGiftDocument>.Update
                            .Set(d => d.NameHidden, true)
                            .Set(d => d.MessageText, null)
                            .Set(d => d.FromUserId, 0));
            }

            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -dropCost);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -dropCost, title: "Remove original details");

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleInvoice)
        {
            var uniqueCol = mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts");
            var uniqueDoc = await uniqueCol.Find(d => d.Slug == resaleInvoice.Slug).FirstOrDefaultAsync();
            if (uniqueDoc == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            // Layer 206: currency is selected by the `ton` flag on the invoice or
            // forced to TON when the gift is flagged resale_ton_only.
            bool useTon = resaleInvoice.Ton || uniqueDoc!.ResaleTonOnly;
            long price = useTon ? uniqueDoc!.ResellTon : uniqueDoc!.ResellStars;
            if (price <= 0) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            long buyerBalance = useTon
                ? await TonBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId)
                : await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
            if (buyerBalance < price) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

            var toPeer2 = peerHelper.GetPeer(resaleInvoice.ToId, input.UserId)!;
            var newOwnerUserId = toPeer2.PeerType == PeerType.User ? toPeer2.PeerId : 0L;
            // If ToId resolves to 0 (e.g. inputPeerSelf not resolved), fallback to buyer
            if (newOwnerUserId == 0 && toPeer2.PeerType != PeerType.Channel) newOwnerUserId = input.UserId;
            var newOwnerChannelId = toPeer2.PeerType == PeerType.Channel ? toPeer2.PeerId : 0L;
            var sellerUserId = uniqueDoc.OwnerUserId;

            // Prevent buying your own gift
            if (sellerUserId == input.UserId || (newOwnerUserId > 0 && newOwnerUserId == sellerUserId))
                RpcErrors.RpcErrors400.StargiftOwnerInvalid.ThrowRpcError();

            // Debit buyer + credit seller (85 % after fee, same as Telegram).
            long sellerReceives = (long)Math.Floor(price * 0.85);
            var unit = useTon ? "TON" : "⭐️";
            if (useTon)
            {
                await TonBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -price);
                await TonBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -price, gift: true, title: $"Buy {uniqueDoc.Title} #{uniqueDoc.Num}");
                await TonBalanceHelper.AddBalanceAsync(mongoDatabase, sellerUserId, sellerReceives);
                await TonBalanceHelper.AddTransactionAsync(mongoDatabase, sellerUserId, sellerReceives, gift: true, title: $"Sold {uniqueDoc.Title} #{uniqueDoc.Num}");
            }
            else
            {
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -price);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -price, gift: true, title: $"Buy {uniqueDoc.Title} #{uniqueDoc.Num}");
                await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, sellerUserId, sellerReceives);
                await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, sellerUserId, sellerReceives, gift: true, title: $"Sold {uniqueDoc.Title} #{uniqueDoc.Num}");
            }

            // Transfer ownership. Both currency-specific listings are cleared and
            // the initial-sale / last-resale columns are refreshed for the matching
            // currency so future stats show the last paid amount.
            var ownershipUpdate = Builders<UniqueStarGiftDocument>.Update
                .Set(d => d.OwnerUserId, newOwnerUserId)
                .Set(d => d.OwnerChannelId, newOwnerChannelId)
                .Set(d => d.ResellStars, 0L)
                .Set(d => d.ResellTon, 0L);
            ownershipUpdate = useTon
                ? ownershipUpdate.Set(d => d.LastResaleTon, price)
                : ownershipUpdate.Set(d => d.LastResaleStars, price).Set(d => d.InitialSaleStars, price);
            await uniqueCol.UpdateOneAsync(d => d.UniqueId == uniqueDoc.UniqueId, ownershipUpdate);

            var savedCol3 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            await savedCol3.DeleteOneAsync(d => d.IsUnique && d.UniqueSlug == uniqueDoc.Slug);
            await savedCol3.InsertOneAsync(new SavedStarGiftDocument
            {
                OwnerUserId = newOwnerUserId,
                OwnerChannelId = newOwnerChannelId,
                FromUserId = sellerUserId,
                GiftId = uniqueDoc.GiftId,
                Stars = 0,
                IsUnique = true,
                UniqueSlug = uniqueDoc.Slug,
                RandomId = uniqueDoc.UniqueId,
                Saved = true,
                Date = DateTime.UtcNow.ToTimestamp(),
                DocumentId = uniqueDoc.DocumentId,
                DocumentAccessHash = uniqueDoc.DocumentAccessHash,
                FileReference = uniqueDoc.FileReference,
                DocumentDate = uniqueDoc.DocumentDate,
                MimeType = uniqueDoc.MimeType,
                DocumentSize = uniqueDoc.DocumentSize,
                DcId = uniqueDoc.DcId,
            });

            // Notify seller
            uniqueDoc.OwnerUserId = newOwnerUserId;
            uniqueDoc.OwnerChannelId = newOwnerChannelId;
            var sellerMsg = $"Your gift {uniqueDoc.Title} #{uniqueDoc.Num} was sold for {price} {unit}. Your balance has been topped up by {sellerReceives} {unit}.";
            await messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with { UserId = MyTelegramConsts.NotificationServiceUserId, Layer = MyTelegramConsts.Layer, Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid() },
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.User, sellerUserId),
                sellerMsg,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.Text,
                messageType: MessageType.Text
            )]);

            // Send gift action to buyer. Layer 206: the resale price is surfaced to
            // the client via messageActionStarGiftUnique.resale_amount so the
            // buyer/seller chat UI can show "sold for N TON" or "sold for N ⭐️".
            if (newOwnerUserId > 0)
            {
                var uniqueTl2 = UniqueStarGiftHelper.ToTl(uniqueDoc);
                IStarsAmount resaleAmount = useTon
                    ? new TStarsTonAmount { Amount = price }
                    : new TStarsAmount { Amount = price, Nanos = 0 };
                var action2 = new TMessageActionStarGiftUnique
                {
                    Gift = uniqueTl2,
                    Transferred = true,
                    Saved = true,
                    FromId = new TPeerUser { UserId = sellerUserId },
                    Peer = new TPeerUser { UserId = newOwnerUserId },
                    ResaleAmount = resaleAmount,
                };
                await messageAppService.SendMessageAsync([new SendMessageInput(
                    RequestInfo.Empty with { UserId = MyTelegramConsts.NotificationServiceUserId, Layer = MyTelegramConsts.Layer, Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid() },
                    MyTelegramConsts.NotificationServiceUserId,
                    new Peer(PeerType.User, newOwnerUserId),
                    string.Empty,
                    Random.Shared.NextInt64(),
                    sendMessageType: SendMessageType.MessageService,
                    messageType: MessageType.Text,
                    messageAction: action2
                )]);
            }

            // Push balance updates to both parties.
            if (useTon)
            {
                await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
                await BalancePushHelper.PushTonBalanceAsync(objectMessageSender, mongoDatabase, sellerUserId);
            }
            else
            {
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, input.UserId);
                await BalancePushHelper.PushStarsBalanceAsync(objectMessageSender, mongoDatabase, sellerUserId);
            }

            return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
        }

        if (obj.Invoice is not TInputInvoiceStarGift starGiftInvoice)
            throw new NotImplementedException();

        var collection = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var gift = await collection.Find(d => d.GiftId == starGiftInvoice.GiftId).FirstOrDefaultAsync();
        if (gift == null)
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Check and deduct balance
        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < gift!.Stars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        if (gift!.Limited)
        {
            var updated = await collection.FindOneAndUpdateAsync(
                Builders<StarGiftDocument>.Filter.And(
                    Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, starGiftInvoice.GiftId),
                    Builders<StarGiftDocument>.Filter.Gt(d => d.AvailabilityRemains, 0)
                ),
                Builders<StarGiftDocument>.Update.Inc(d => d.AvailabilityRemains, -1),
                new FindOneAndUpdateOptions<StarGiftDocument> { ReturnDocument = ReturnDocument.After }
            );
            if (updated == null)
                RpcErrors.RpcErrors400.StargiftUsageLimited.ThrowRpcError();
            gift = updated;
            var now = DateTime.UtcNow.ToTimestamp();
            // Set FirstSaleDate only if not yet set, always update LastSaleDate
            await collection.UpdateOneAsync(
                Builders<StarGiftDocument>.Filter.And(
                    Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, gift!.GiftId),
                    Builders<StarGiftDocument>.Filter.Eq(d => d.FirstSaleDate, null)
                ),
                Builders<StarGiftDocument>.Update.Set(d => d.FirstSaleDate, now)
            );
            var soldOut = gift!.AvailabilityRemains == 0;
            await collection.UpdateOneAsync(
                Builders<StarGiftDocument>.Filter.Eq(d => d.GiftId, gift.GiftId),
                Builders<StarGiftDocument>.Update.Set(d => d.LastSaleDate, now).Set(d => d.SoldOut, soldOut)
            );
        }

        var toPeer = peerHelper.GetPeer(starGiftInvoice.Peer, input.UserId)!;
        var isChannel = toPeer.PeerType == PeerType.Channel;

        // Only allow gifts to users and broadcast channels, not groups
        if (isChannel)
        {
            var channelReadModel = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(new BsonDocument("ChannelId", toPeer.PeerId))
                .Project(new BsonDocument { { "Broadcast", 1 } })
                .FirstOrDefaultAsync();
            if (channelReadModel == null || !channelReadModel.GetValue("Broadcast", false).AsBoolean)
                RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        var starGift = new TStarGift
        {
            Id = gift!.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            Sticker = new TDocument
            {
                Id = gift.DocumentId,
                AccessHash = gift.DocumentAccessHash,
                FileReference = gift.FileReference,
                Date = gift.DocumentDate,
                MimeType = gift.MimeType,
                Size = gift.DocumentSize,
                DcId = gift.DcId,
                Attributes = [new TDocumentAttributeSticker { Alt = "🎁", Stickerset = new TInputStickerSetEmpty() }],
            },
        };

        var messageAction = new TMessageActionStarGift
        {
            Gift = starGift,
            NameHidden = starGiftInvoice.HideName,
            CanUpgrade = gift.UpgradeStars.HasValue,
            ConvertStars = gift.ConvertStars,
            Message = starGiftInvoice.HideName ? null : starGiftInvoice.Message,
            Peer = isChannel ? new TPeerChannel { ChannelId = toPeer.PeerId } : null,
            SavedId = isChannel ? 0L : null,
            FromId = isChannel && !starGiftInvoice.HideName ? new TPeerUser { UserId = input.UserId } : null,
        };

        var randomId = Random.Shared.NextInt64();

        if (isChannel)
        {
            // Notify each channel admin via private message from 777000
            var channelAdmins = await mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel")
                .Find(new BsonDocument("ChannelId", toPeer.PeerId))
                .Project(new BsonDocument("AdminList", 1))
                .FirstOrDefaultAsync();

            var adminUserIds = channelAdmins?["AdminList"]?.AsBsonArray
                .Select(a => a["UserId"].AsInt64)
                .Distinct()
                .ToList() ?? [];

            var notifyTasks = adminUserIds.Select(adminId => messageAppService.SendMessageAsync([new SendMessageInput(
                RequestInfo.Empty with
                {
                    UserId = MyTelegramConsts.NotificationServiceUserId,
                    Layer = MyTelegramConsts.Layer,
                    Date = DateTime.UtcNow.ToTimestamp(),
                    RequestId = Guid.NewGuid()
                },
                MyTelegramConsts.NotificationServiceUserId,
                new Peer(PeerType.User, adminId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: messageAction
            )]));
            await Task.WhenAll(notifyTasks);
        }
        else
        {
            var sendMessageInput = new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                toPeer,
                string.Empty,
                randomId,
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: messageAction
            );
            await messageAppService.SendMessageAsync([sendMessageInput]);
        }

        var savedGifts = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        await savedGifts.InsertOneAsync(new SavedStarGiftDocument
        {
            Id = ObjectId.GenerateNewId(),
            OwnerUserId = isChannel ? 0 : toPeer.PeerId,
            OwnerChannelId = isChannel ? toPeer.PeerId : 0,
            FromUserId = input.UserId,
            GiftId = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            NameHidden = starGiftInvoice.HideName,
            Saved = true,
            Date = DateTime.UtcNow.ToTimestamp(),
            MessageText = starGiftInvoice.HideName ? null : starGiftInvoice.Message?.Text,
            RandomId = randomId,
            DocumentId = gift.DocumentId,
            DocumentAccessHash = gift.DocumentAccessHash,
            FileReference = gift.FileReference,
            DocumentDate = gift.DocumentDate,
            MimeType = gift.MimeType,
            DocumentSize = gift.DocumentSize,
            DcId = gift.DcId,
        });

        // Deduct stars from sender, add transaction
        var totalCost = gift!.Stars + (starGiftInvoice.IncludeUpgrade && gift.UpgradeStars.HasValue ? gift.UpgradeStars.Value : 0);
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -totalCost);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -totalCost, gift: true,
            peerUserId: isChannel ? null : toPeer.PeerId,
            peerChannelId: isChannel ? toPeer.PeerId : null);

        if (starGiftInvoice.IncludeUpgrade && gift.UpgradeStars.HasValue)
        {
            var hash = Guid.NewGuid().ToString("N");
            await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                .UpdateOneAsync(d => d.RandomId == randomId,
                    Builders<SavedStarGiftDocument>.Update
                        .Set(d => d.PrepaidUpgrade, true)
                        .Set(d => d.PrepaidUpgradeHash, hash)
                        .Set(d => d.UpgradeStars, 0)); // already paid
            messageAction.PrepaidUpgrade = true;
            messageAction.PrepaidUpgradeHash = hash;
        }

        return new TPaymentResult { Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 } };
    }

    private async Task ActivatePremiumAsync(long userId, int months)
    {
        var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");

        // Check if user already has Premium
        var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
        var user = await userCol.Find(userFilter).FirstOrDefaultAsync();

        if (user == null)
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();

        bool alreadyHasPremium = user.Contains("Premium") && user["Premium"].AsBoolean;

        // Set Premium flag
        var update = Builders<BsonDocument>.Update.Set("Premium", true);
        await userCol.UpdateOneAsync(userFilter, update);

        // Create/update premium subscription
        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiresAt = now + (months * 30 * 24 * 3600);

        var subCol = mongoDatabase.GetCollection<BsonDocument>("premium_subscriptions");
        await subCol.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("UserId", userId),
            new BsonDocument
            {
                ["UserId"] = userId,
                ["Months"] = months,
                ["ActivatedAt"] = now,
                ["ExpiresAt"] = expiresAt,
                ["Currency"] = "XTR"
            },
            new ReplaceOptions { IsUpsert = true });

        // Grant 4 boosts ONLY if user didn't have Premium before
        if (!alreadyHasPremium)
        {
            var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
            var expires = now + (86400 * 365); // 1 year

            // Find next available slot
            var existingBoosts = await boostCol.Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
                .ToListAsync();

            int nextSlot = 1;
            if (existingBoosts.Count > 0)
            {
                var usedSlots = existingBoosts.Select(b => b["Slot"].AsInt32).ToHashSet();
                while (usedSlots.Contains(nextSlot))
                    nextSlot++;
            }

            // Add 4 boosts
            for (int i = 0; i < 4; i++)
            {
                await boostCol.InsertOneAsync(new BsonDocument
                {
                    ["UserId"] = userId,
                    ["Slot"] = nextSlot + i,
                    ["ChannelId"] = 0L,
                    ["Date"] = now,
                    ["Expires"] = expires
                });
            }
        }
    }
}
