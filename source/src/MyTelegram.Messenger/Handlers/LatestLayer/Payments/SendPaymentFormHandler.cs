using MyTelegram.Schema.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
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
    {
        // Auction bid (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
            return await HandleAuctionBidAsync(input, auctionBid);

        // Star Gift sending (XTR payment, no Stripe)
        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
            return await HandleStarGiftSendAsync(input, starGiftInvoice);

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
                            var credCol = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
                            await credCol.ReplaceOneAsync(
                                x => x.UserId == input.UserId && x.PaymentMethodId == paymentMethodId,
                                new SavedPaymentCredentialDocument { UserId = input.UserId, PaymentMethodId = paymentMethodId, Title = title, CreatedAt = DateTime.UtcNow },
                                new ReplaceOptions { IsUpsert = true });
                        }
                    }
                }
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
            await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, intent.Stars,
                title: $"Stripe top-up: {intent.Stars} stars");
        }

        await col.DeleteOneAsync(x => x.Id == intent.Id);

        return new TPaymentResult
        {
            Updates = new TUpdates { Updates = [], Users = [], Chats = [], Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
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
            Updates = new TUpdates { Updates = [], Users = [], Chats = [], Date = DateTime.UtcNow.ToTimestamp(), Seq = 0 }
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
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

        // Calculate total cost
        long totalStars = gift!.Stars;
        if (invoice.IncludeUpgrade && gift.UpgradeStars.HasValue)
            totalStars += gift.UpgradeStars.Value;

        // Check sender's balance
        var senderBalance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (senderBalance < totalStars)
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();

        // Get recipient peer
        var recipientPeer = peerHelper.GetPeer(invoice.Peer, input.UserId);
        if (recipientPeer == null)
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();

        // Deduct stars from sender
        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -totalStars);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -totalStars,
            title: $"Gift: {gift.Title}", peerUserId: recipientPeer.PeerId);

        // Create SavedStarGiftDocument for recipient
        var now = DateTime.UtcNow.ToTimestamp();
        var randomId = Random.Shared.NextInt64();
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
            Updates = new TUpdates { Updates = [], Users = [], Chats = [], Date = now, Seq = 0 }
        };
    }
}
