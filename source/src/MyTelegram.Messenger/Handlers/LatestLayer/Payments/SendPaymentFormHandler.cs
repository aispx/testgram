using MyTelegram.Schema.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Services.StarGifts;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class SendPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    ILogger<SendPaymentFormHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestSendPaymentForm, MyTelegram.Schema.Payments.IPaymentResult>
{
    protected override async Task<MyTelegram.Schema.Payments.IPaymentResult> HandleCoreAsync(
        IRequestInput input, MyTelegram.Schema.Payments.RequestSendPaymentForm obj)
    { // Force rebuild 407
        // Auction bid (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
            return await HandleAuctionBidAsync(input, auctionBid);

        // Star Gift sending (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
            return await HandleStarGiftSendAsync(input, starGiftInvoice);

        // Premium gift via Stars is handled by SendStarsFormHandler (payments.sendStarsForm)
        // This handler only processes Stripe payments (USD/EUR)
        // if (obj.Invoice is TInputInvoicePremiumGiftStars premiumStarsInvoice)
        //     return await HandlePremiumGiftStarsAsync(input, premiumStarsInvoice);

        // Premium gift code via Stripe
        if (obj.Invoice is TInputInvoicePremiumGiftCode premiumGiftCode)
            return await HandlePremiumGiftCodeAsync(input, premiumGiftCode, obj);

        if (obj.Invoice is not TInputInvoiceStars)
            throw new NotImplementedException();

        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");
        var intent = await col.Find(x => x.FormId == obj.FormId).FirstOrDefaultAsync();

        logger.LogInformation("SendPaymentForm: userId={UserId} formId={FormId} intentFound={Found} recipientUserId={RecipientUserId}",
            input.UserId, obj.FormId, intent != null, intent?.RecipientUserId ?? -1);

        if (intent == null)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        var stripe = options.Value.Stripe;
        var credentialsJson = (obj.Credentials as TInputPaymentCredentials)?.Data?.Data ?? "{}";

        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            string? stripeTokenId = null;
            try { var doc = JsonDocument.Parse(credentialsJson).RootElement; if (doc.TryGetProperty("id", out var idProp)) stripeTokenId = idProp.GetString(); } catch { }

            if (!string.IsNullOrEmpty(stripeTokenId))
            {
                var (existingStatus, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent!.PaymentIntentId);
                if (existingStatus != "succeeded" && existingStatus != "requires_capture")
                {
                    var paymentMethodId = await ConfirmPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId, stripeTokenId!);
                    var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
                    if (status != "succeeded" && status != "requires_capture")
                        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

                    // Save card for future use
                    if (paymentMethodId != null)
                    {
                        var title = await StripeHelper.GetPaymentMethodTitleAsync(stripe.SecretKey, paymentMethodId);
                        if (title != null)
                        {
                            try
                            {
                                var credCol = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
                                await credCol.ReplaceOneAsync(
                                    x => x.UserId == input.UserId && x.PaymentMethodId == paymentMethodId,
                                    new SavedPaymentCredentialDocument { UserId = input.UserId, PaymentMethodId = paymentMethodId, Title = title, CreatedAt = DateTime.UtcNow },
                                    new ReplaceOptions { IsUpsert = true });
                            }
                            catch (MongoWriteException ex) when (ex.WriteError?.Code == 11000)
                            {
                                // Ignore duplicate key error - card already saved
                                logger.LogWarning("Duplicate payment credential ignored: {PaymentMethodId}", paymentMethodId);
                            }
                        }
                    }
                }
            }
        }

        // Check if this is a giveaway payment
        bool isGiveaway = intent!.GiveawayPurpose != null;
        logger.LogInformation("[SendPaymentForm] isGiveaway={IsGiveaway}, GiveawayPurpose length={Length}", isGiveaway, intent.GiveawayPurpose?.Length ?? 0);

        if (isGiveaway)
        {
            logger.LogInformation("[SendPaymentForm] Launching giveaway...");
            // Direct client payment → launch giveaway immediately
            // Bot/Fragment flow would use different invoice type or come from specific bot

            await col.DeleteOneAsync(x => x.Id == intent.Id);

            var purposeBytes = intent.GiveawayPurpose!;
            var buffer = new ReadOnlyMemory<byte>(purposeBytes);
            var purpose = buffer.Read<IInputStorePaymentPurpose>();
            logger.LogInformation("[SendPaymentForm] Purpose type: {Type}", purpose?.GetType().Name);

            if (purpose is TInputStorePaymentStarsGiveaway starsGiveaway)
            {
                logger.LogInformation("[SendPaymentForm] Launching Stars giveaway");
                // Launch Stars giveaway immediately
                return await LaunchStarsGiveawayAsync(input, starsGiveaway);
            }
            else
            {
                logger.LogInformation("[SendPaymentForm] Launching Premium giveaway");
                // Launch Premium giveaway immediately
                var premiumGiveaway = (TInputStorePaymentPremiumGiveaway)purpose;
                return await LaunchPremiumGiveawayAsync(input, premiumGiveaway);
            }
        }

        bool isGift = intent!.RecipientUserId > 0;
        long creditUserId = isGift ? intent.RecipientUserId : input.UserId;

        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, creditUserId, intent.Stars);

        if (isGift)
        {
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -intent.Stars,
                title: $"Gift {intent.Stars} Stars", peerUserId: intent.RecipientUserId);
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, creditUserId, intent.Stars,
                gift: true, title: $"Gift {intent.Stars} Stars", peerUserId: input.UserId);

            var giftPurpose = (obj.Invoice as TInputInvoiceStars)?.Purpose as TInputStorePaymentStarsGift;
            var messageAction = new TMessageActionGiftStars
            {
                Currency = giftPurpose?.Currency ?? "USD",
                Amount = giftPurpose?.Amount ?? 0,
                Stars = intent.Stars,
                TransactionId = intent.PaymentIntentId,
            };
            await messageAppService.SendMessageAsync([new SendMessageInput(
                input.ToRequestInfo() with { ReqMsgId = 0 },
                input.UserId,
                new Peer(PeerType.User, intent.RecipientUserId),
                string.Empty,
                Random.Shared.NextInt64(),
                sendMessageType: SendMessageType.MessageService,
                messageType: MessageType.Text,
                messageAction: messageAction
            )]);
        }
        else
        {
            // Create transaction for recipient (creditUserId) who received the stars
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, creditUserId, intent.Stars,
                title: $"Stripe top-up: {intent.Stars} stars");
        }

        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }

    private static readonly HttpClient Http = new();

    private async Task<IPaymentResult> HandleAuctionBidAsync(IRequestInput input, TInputInvoiceStarGiftAuctionBid auctionBid)
    {
        var auctionCol = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
        var auction = await auctionCol.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
        if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);

        var existingBid = auction.Bids.FirstOrDefault(b => b.UserId == input.UserId && !b.Returned);
        long extraNeeded = existingBid != null ? auctionBid.BidAmount - existingBid.Amount : auctionBid.BidAmount;
        if (balance < extraNeeded) RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Refund old bid if updating
        if (existingBid != null)
        {
            existingBid.Returned = true;
            await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, existingBid.Amount);
        }

        // Deduct new bid
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -auctionBid.BidAmount,
            title: $"Auction bid for gift {auctionBid.GiftId}");

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

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }

    private static async Task<string?> ConfirmPaymentIntentAsync(string secretKey, string paymentIntentId, string tokenId)
    {
        var paymentMethodId = await StripeHelper.CreatePaymentMethodAsync(secretKey, tokenId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/payment_intents/{paymentIntentId}/confirm");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["payment_method"] = paymentMethodId });
        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe confirm error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return paymentMethodId;
    }

    private async Task<IPaymentResult> HandleStarGiftSendAsync(IRequestInput input, TInputInvoiceStarGift invoice)
    {
        // Get gift from MongoDB
        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var gift = await giftCol.Find(d => d.GiftId == invoice.GiftId).FirstOrDefaultAsync();
        if (gift == null)
            throw new RpcException(new RpcError(400, "STARGIFT_NOT_FOUND"));

        // Check if gift is sold out (STARGIFT_USAGE_LIMITED)
        if (gift!.SoldOut || (gift.AvailabilityRemains.HasValue && gift.AvailabilityRemains.Value <= 0))
            throw new RpcException(new RpcError(400, "STARGIFT_USAGE_LIMITED"));

        // Check per-user limit (STARGIFT_USER_USAGE_LIMITED)
        if (gift.LimitedPerUser && gift.PerUserTotal.HasValue && gift.PerUserRemains.HasValue)
        {
            var userGiftCount = await mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts")
                .CountDocumentsAsync(d =>
                    d.FromUserId == input.UserId &&
                    d.GiftId == gift.GiftId);

            if (userGiftCount >= gift.PerUserTotal.Value)
                throw new RpcException(new RpcError(400, "STARGIFT_USER_USAGE_LIMITED"));
        }

        // Check if gift is locked (not yet available for purchase)
        var currentTime = DateTime.UtcNow.ToTimestamp();
        if (gift.LockedUntilDate.HasValue && gift.LockedUntilDate.Value > currentTime)
            throw new RpcException(new RpcError(400, "STARGIFT_INVALID"));

        // Calculate total cost
        long totalStars = gift.Stars;
        if (invoice.IncludeUpgrade && gift.UpgradeStars.HasValue)
            totalStars += gift.UpgradeStars.Value;

        // Check sender's balance
        var senderBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (senderBalance < totalStars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Get recipient peer
        var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
        if (recipientPeer == null)
            throw new RpcException(new RpcError(400, "STARGIFT_PEER_INVALID"));

        // Deduct stars from sender
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -totalStars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -totalStars,
            title: $"Gift: {gift.Title}", peerUserId: recipientPeer.PeerId);

        // Decrease availability if limited
        if (gift.Limited && gift.AvailabilityRemains.HasValue)
        {
            await giftCol.UpdateOneAsync(
                d => d.GiftId == gift.GiftId,
                Builders<StarGiftDocument>.Update.Inc(d => d.AvailabilityRemains, -1)
            );
        }

        // Decrease per-user availability if limited per user
        if (gift.LimitedPerUser && gift.PerUserRemains.HasValue)
        {
            await giftCol.UpdateOneAsync(
                d => d.GiftId == gift.GiftId,
                Builders<StarGiftDocument>.Update.Inc(d => d.PerUserRemains, -1)
            );
        }

        // Create SavedStarGiftDocument for recipient
        var now = DateTime.UtcNow.ToTimestamp();
        var randomId = Random.Shared.NextInt64();

        // Set 21-day transfer cooldown for new gifts (from telelakel)
        // Note: Some gifts have cooldown removed, but we apply it by default
        int? canResellAt = now + (21 * 24 * 60 * 60);

        var savedGift = new SavedStarGiftDocument
        {
            OwnerUserId = recipientPeer.PeerType == PeerType.User ? recipientPeer.PeerId : 0,
            OwnerChannelId = recipientPeer.PeerType == PeerType.Channel ? recipientPeer.PeerId : 0,
            FromUserId = input.UserId,
            MessageId = 0, // Will be set after message is created
            GiftId = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            NameHidden = invoice.HideName,
            Saved = false, // Not saved to profile by default
            Date = now,
            MessageText = (invoice.Message as TTextWithEntities)?.Text,
            RandomId = randomId,
            PinnedToTop = false,
            IsUnique = false,
            PrepaidUpgrade = invoice.IncludeUpgrade,
            CanResellAt = canResellAt,
            DocumentId = gift.DocumentId,
            DocumentAccessHash = gift.DocumentAccessHash,
            FileReference = gift.FileReference,
            DocumentDate = gift.DocumentDate,
            MimeType = gift.MimeType,
            DocumentSize = gift.DocumentSize,
            DcId = gift.DcId,
        };

        var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
        await savedCol.InsertOneAsync(savedGift);

        // Build TStarGift for message action
        var starGiftTl = new TStarGift
        {
            Id = gift.GiftId,
            Stars = gift.Stars,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            Limited = gift.Limited,
            SoldOut = gift.SoldOut,
            Birthday = gift.Birthday,
            RequirePremium = gift.RequirePremium,
            LimitedPerUser = gift.LimitedPerUser,
            AvailabilityRemains = gift.AvailabilityRemains,
            AvailabilityTotal = gift.AvailabilityTotal,
            FirstSaleDate = gift.FirstSaleDate,
            LastSaleDate = gift.LastSaleDate,
            ResellMinStars = gift.ResellMinStars,
            Title = gift.Title,
            PerUserTotal = gift.PerUserTotal,
            PerUserRemains = gift.PerUserRemains,
            LockedUntilDate = gift.LockedUntilDate,
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

        // Create service message with messageActionStarGift
        var messageAction = new TMessageActionStarGift
        {
            NameHidden = invoice.HideName,
            Saved = false,
            CanUpgrade = gift.UpgradeStars.HasValue && !invoice.IncludeUpgrade,
            PrepaidUpgrade = invoice.IncludeUpgrade,
            Gift = starGiftTl,
            Message = invoice.Message,
            ConvertStars = gift.ConvertStars,
            UpgradeStars = gift.UpgradeStars,
            FromId = invoice.HideName ? null : new TPeerUser { UserId = input.UserId },
            // Set Peer and SavedId for channel gifts (both use flags.12)
            Peer = recipientPeer.PeerType == PeerType.Channel
                ? new TPeerChannel { ChannelId = recipientPeer.PeerId }
                : null,
            SavedId = recipientPeer.PeerType == PeerType.Channel
                ? savedGift.RandomId
                : null,
        };

        await messageAppService.SendMessageAsync([new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            recipientPeer,
            string.Empty,
            randomId,
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: messageAction
        )]);

        logger.LogInformation("Star gift sent: giftId={GiftId} from={From} to={To} stars={Stars} prepaidUpgrade={PrepaidUpgrade}",
            gift.GiftId, input.UserId, recipientPeer.PeerId, totalStars, invoice.IncludeUpgrade);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = now, Seq = 0 }
        };
    }

    // Removed: HandlePremiumGiftStarsAsync - now handled by SendStarsFormHandler

    private async Task<IPaymentResult> LaunchStarsGiveawayAsync(IRequestInput input, TInputStorePaymentStarsGiveaway giveaway)
    {
        // Validate peer
        var targetPeer = peerHelper.GetPeer(giveaway.BoostPeer, input.UserId);
        if (targetPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Extract giveaway data
        var winners = giveaway.Users;
        var untilDate = giveaway.UntilDate;
        var onlyNewSubscribers = giveaway.OnlyNewSubscribers;
        var winnersAreVisible = giveaway.WinnersAreVisible;
        var stars = giveaway.Stars;
        var prizeDescription = giveaway.PrizeDescription;

        var additionalChannels = new List<long>();
        if (giveaway.AdditionalPeers != null)
        {
            foreach (var peer in giveaway.AdditionalPeers)
            {
                var p = peerHelper.GetPeer(peer, input.UserId);
                if (p.PeerType == PeerType.Channel)
                    additionalChannels.Add(p.PeerId);
            }
        }

        var countries = giveaway.CountriesIso2?.ToList() ?? new List<string>();

        // Create messageMediaGiveaway
        var media = new TMessageMediaGiveaway
        {
            OnlyNewSubscribers = onlyNewSubscribers,
            WinnersAreVisible = winnersAreVisible,
            Channels = new TVector<long> { targetPeer.PeerId },
            CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
            PrizeDescription = prizeDescription,
            UntilDate = untilDate,
            Quantity = winners,
            Stars = stars
        };

        // Add additional channels if any
        if (additionalChannels.Count > 0)
        {
            media.Channels.AddRange(additionalChannels);
        }

        // Send giveaway message to channel
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            targetPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.Media,
            messageType: MessageType.Text,
            media: media
        );

        await messageAppService.SendMessageAsync([sendInput]);

        // Create giveaway document in MongoDB
        var giveawayCol = mongoDatabase.GetCollection<BsonDocument>("giveaways");
        var giveawayId = $"giveaway-{targetPeer.PeerId}-{giveaway.RandomId}";

        await giveawayCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = giveawayId,
            ["ChannelId"] = targetPeer.PeerId,
            ["RandomId"] = giveaway.RandomId,
            ["MsgId"] = 0, // Will be updated when message is created
            ["Type"] = "stars",
            ["Winners"] = winners,
            ["Stars"] = stars,
            ["UntilDate"] = untilDate,
            ["OnlyNewSubscribers"] = onlyNewSubscribers,
            ["WinnersAreVisible"] = winnersAreVisible,
            ["PrizeDescription"] = prizeDescription ?? "",
            ["Countries"] = new BsonArray(countries),
            ["AdditionalChannels"] = new BsonArray(additionalChannels),
            ["Status"] = "active",
            ["StartDate"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["CreatedBy"] = input.UserId
        });

        logger.LogInformation("Stars giveaway launched: stars={Stars} winners={Winners} channel={Channel}",
            stars, winners, targetPeer.PeerId);

        // Return empty Updates (message will arrive via push)
        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seq = 0
            }
        };
    }

    private async Task<IPaymentResult> LaunchPremiumGiveawayAsync(IRequestInput input, TInputStorePaymentPremiumGiveaway giveaway)
    {
        logger.LogInformation("[LaunchPremiumGiveaway] START - UserId={UserId}", input.UserId);
        logger.LogInformation("[LaunchPremiumGiveaway] BoostPeer type: {Type}", giveaway.BoostPeer?.GetType().Name);

        // Validate peer
        var targetPeer = peerHelper.GetPeer(giveaway.BoostPeer, input.UserId);
        logger.LogInformation("[LaunchPremiumGiveaway] Target peer: {PeerType} {PeerId}", targetPeer.PeerType, targetPeer.PeerId);

        if (targetPeer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // For Premium giveaway, we need to get winners/months from the payment form
        // Since they're not in TInputStorePaymentPremiumGiveaway, use defaults
        int winners = 10; // Default winner count
        int months = 12; // Default premium duration
        var untilDate = giveaway.UntilDate;
        var onlyNewSubscribers = giveaway.OnlyNewSubscribers;
        var winnersAreVisible = giveaway.WinnersAreVisible;
        var prizeDescription = giveaway.PrizeDescription;

        var additionalChannels = new List<long>();
        if (giveaway.AdditionalPeers != null)
        {
            foreach (var peer in giveaway.AdditionalPeers)
            {
                var p = peerHelper.GetPeer(peer, input.UserId);
                if (p.PeerType == PeerType.Channel)
                    additionalChannels.Add(p.PeerId);
            }
        }

        var countries = giveaway.CountriesIso2?.ToList() ?? new List<string>();

        logger.LogInformation("[LaunchPremiumGiveaway] Creating media: winners={Winners} months={Months} untilDate={UntilDate}", winners, months, untilDate);

        // Create messageMediaGiveaway
        var media = new TMessageMediaGiveaway
        {
            OnlyNewSubscribers = onlyNewSubscribers,
            WinnersAreVisible = winnersAreVisible,
            Channels = new TVector<long> { targetPeer.PeerId },
            CountriesIso2 = countries.Count > 0 ? new TVector<string>(countries) : null,
            PrizeDescription = prizeDescription,
            UntilDate = untilDate,
            Quantity = winners,
            Months = months
        };

        // Add additional channels if any
        if (additionalChannels.Count > 0)
        {
            media.Channels.AddRange(additionalChannels);
        }

        logger.LogInformation("[LaunchPremiumGiveaway] Sending message to channel {ChannelId}", targetPeer.PeerId);

        // Generate RandomId for message
        var randomId = Random.Shared.NextInt64();

        // Send giveaway message to channel
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            targetPeer,
            string.Empty,
            randomId,
            sendMessageType: SendMessageType.Media,
            messageType: MessageType.Text,
            media: media
        );

        await messageAppService.SendMessageAsync([sendInput]);

        logger.LogInformation("[LaunchPremiumGiveaway] Message sent successfully");

        // Save giveaway to MongoDB for GetGiveawayInfoHandler
        // Note: We use RandomId as temporary identifier since SendMessageAsync doesn't return MessageId
        // The actual MessageId will be assigned by event sourcing
        var giveawayCol = mongoDatabase.GetCollection<BsonDocument>("giveaways");
        await giveawayCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"giveaway-{targetPeer.PeerId}-{randomId}",
            ["ChannelId"] = targetPeer.PeerId,
            ["RandomId"] = randomId,
            ["MsgId"] = 0, // Will be updated by domain event handler
            ["Type"] = "premium",
            ["Winners"] = winners,
            ["Months"] = months,
            ["UntilDate"] = untilDate,
            ["OnlyNewSubscribers"] = onlyNewSubscribers,
            ["WinnersAreVisible"] = winnersAreVisible,
            ["PrizeDescription"] = prizeDescription ?? "",
            ["Countries"] = new BsonArray(countries),
            ["AdditionalChannels"] = new BsonArray(additionalChannels),
            ["Status"] = "active",
            ["StartDate"] = DateTime.UtcNow.ToTimestamp(),
            ["CreatedBy"] = input.UserId
        });

        logger.LogInformation("Premium giveaway launched: months={Months} winners={Winners} channel={Channel}",
            months, winners, targetPeer.PeerId);

        // Return empty Updates (message will arrive via push)
        return new TPaymentResult
        {
            Updates = new TUpdates
            {
                Updates = new TVector<IUpdate>(),
                Users = new TVector<IUser>(),
                Chats = new TVector<IChat>(),
                Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seq = 0
            }
        };
    }

    private async Task<IPaymentResult> HandlePremiumGiftCodeAsync(IRequestInput input, TInputInvoicePremiumGiftCode invoice, RequestSendPaymentForm obj)
    {
        // This is Stripe payment for premium gift code
        var col = mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents");
        var intent = await col.Find(x => x.FormId == obj.FormId).FirstOrDefaultAsync();

        if (intent == null)
            RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();

        var stripe = options.Value.Stripe;
        var credentialsJson = (obj.Credentials as TInputPaymentCredentials)?.Data?.Data ?? "{}";

        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            string? stripeTokenId = null;
            try { var doc = JsonDocument.Parse(credentialsJson).RootElement; if (doc.TryGetProperty("id", out var idProp)) stripeTokenId = idProp.GetString(); } catch { }

            if (!string.IsNullOrEmpty(stripeTokenId))
            {
                var (existingStatus, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent!.PaymentIntentId);
                if (existingStatus != "succeeded" && existingStatus != "requires_capture")
                {
                    await ConfirmPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId, stripeTokenId!);
                    var (status, _, _) = await StripeHelper.GetPaymentIntentAsync(stripe.SecretKey, intent.PaymentIntentId);
                    if (status != "succeeded" && status != "requires_capture")
                        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
                }
            }
        }

        // Grant premium based on purpose
        if (invoice.Purpose is TInputStorePaymentPremiumGiftCode giftCode)
        {
            // Gift code for multiple users
            var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var expireDate = DateTime.UtcNow.AddMonths(invoice.Option.Months).ToTimestamp();

            // Create boost slots for gifter (3 boosts per gift)
            var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
            var gifterBoosts = new List<BsonDocument>();
            var recipientBoosts = new List<BsonDocument>();
            var expiresTimestamp = (int)DateTimeOffset.UtcNow.AddMonths(invoice.Option.Months).ToUnixTimeSeconds();

            for (int i = 0; i < giftCode.Users.Count; i++)
            {
                var userId = giftCode.Users[i];
                var recipientUserId = userId is TInputUser inputUser ? inputUser.UserId : 0;
                if (recipientUserId > 0)
                {
                    await userCol.UpdateOneAsync(
                        Builders<BsonDocument>.Filter.Eq("UserId", recipientUserId),
                        Builders<BsonDocument>.Update.Set("Premium", true).Set("PremiumExpireDate", expireDate)
                    );

                    // Send service message
                    var messageAction = new TMessageActionGiftPremium
                    {
                        Currency = invoice.Option.Currency,
                        Amount = invoice.Option.Amount / giftCode.Users.Count,
                        Days = invoice.Option.Months * 30, // Convert months to days
                    };

                    await messageAppService.SendMessageAsync([new SendMessageInput(
                        input.ToRequestInfo() with { ReqMsgId = 0 },
                        input.UserId,
                        new Peer(PeerType.User, recipientUserId),
                        string.Empty,
                        Random.Shared.NextInt64(),
                        sendMessageType: SendMessageType.MessageService,
                        messageType: MessageType.Text,
                        messageAction: messageAction
                    )]);

                    // Create 3 boost slots for gifter
                    for (int slot = 0; slot < 3; slot++)
                    {
                        gifterBoosts.Add(new BsonDocument
                        {
                            ["_id"] = $"boost-gift-{input.UserId}-{recipientUserId}-{slot}-{Guid.NewGuid():N}",
                            ["ChannelId"] = 0, // Not assigned to any channel yet
                            ["UserId"] = input.UserId,
                            ["Slot"] = gifterBoosts.Count + slot,
                            ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["Expires"] = expiresTimestamp,
                            ["Multiplier"] = 1,
                            ["Gift"] = true,
                            ["Giveaway"] = false,
                            ["Unclaimed"] = false
                        });
                    }

                    // Create 1 boost slot for recipient
                    recipientBoosts.Add(new BsonDocument
                    {
                        ["_id"] = $"boost-gift-recipient-{recipientUserId}-{Guid.NewGuid():N}",
                        ["ChannelId"] = 0, // Not assigned to any channel yet
                        ["UserId"] = recipientUserId,
                        ["Slot"] = 0,
                        ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["Expires"] = expiresTimestamp,
                        ["Multiplier"] = 1,
                        ["Gift"] = true,
                        ["Giveaway"] = false,
                        ["Unclaimed"] = false
                    });
                }
            }

            // Insert boost slots
            if (gifterBoosts.Count > 0)
            {
                await boostCol.InsertManyAsync(gifterBoosts);
                logger.LogInformation("Created {Count} boost slots for gifter {UserId}",
                    gifterBoosts.Count, input.UserId);
            }
            if (recipientBoosts.Count > 0)
            {
                await boostCol.InsertManyAsync(recipientBoosts);
                logger.LogInformation("Created {Count} boost slots for recipients",
                    recipientBoosts.Count);
            }

            logger.LogInformation("Premium gift code purchased: from={From} users={Count} months={Months}",
                input.UserId, giftCode.Users.Count, invoice.Option.Months);
        }
        else if (invoice.Purpose is TInputStorePaymentPremiumSubscription)
        {
            // Regular premium subscription for self - 4 boosts
            var userCol = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var expireDate = DateTime.UtcNow.AddMonths(invoice.Option.Months).ToTimestamp();
            await userCol.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                Builders<BsonDocument>.Update.Set("Premium", true).Set("PremiumExpireDate", expireDate)
            );

            // Create 4 boost slots for self-purchase
            var boostCol = mongoDatabase.GetCollection<BsonDocument>("channel_boosts");
            var boosts = new List<BsonDocument>();
            var expiresTimestamp = (int)DateTimeOffset.UtcNow.AddMonths(invoice.Option.Months).ToUnixTimeSeconds();

            for (int slot = 0; slot < 4; slot++)
            {
                boosts.Add(new BsonDocument
                {
                    ["_id"] = $"boost-self-{input.UserId}-{slot}-{Guid.NewGuid():N}",
                    ["ChannelId"] = 0, // Not assigned to any channel yet
                    ["UserId"] = input.UserId,
                    ["Slot"] = slot,
                    ["Date"] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["Expires"] = expiresTimestamp,
                    ["Multiplier"] = 1,
                    ["Gift"] = false,
                    ["Giveaway"] = false,
                    ["Unclaimed"] = false
                });
            }

            await boostCol.InsertManyAsync(boosts);

            logger.LogInformation("Premium subscription purchased: userId={UserId} months={Months} boosts=4",
                input.UserId, invoice.Option.Months);
        }
        else if (intent!.GiveawayPurpose != null)
        {
            // Premium giveaway - launch immediately
            logger.LogInformation("Premium giveaway payment confirmed, launching giveaway...");

            var purposeBytes = intent.GiveawayPurpose;
            var buffer = new ReadOnlyMemory<byte>(purposeBytes);
            var purpose = buffer.Read<IInputStorePaymentPurpose>();

            if (purpose is TInputStorePaymentPremiumGiveaway premiumGiveaway)
            {
                await col.DeleteOneAsync(x => x.Id == intent.Id);
                return await LaunchPremiumGiveawayAsync(input, premiumGiveaway);
            }
        }

        await col.DeleteOneAsync(x => x.Id == intent!.Id);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = new TVector<IUpdate>(), Users = new TVector<IUser>(), Chats = new TVector<IChat>(), Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
        };
    }
}
