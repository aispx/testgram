using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetPaymentFormHandler(
    IMongoDatabase mongoDatabase,
    IOptions<MyTelegramMessengerServerOptions> options,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<RequestGetPaymentForm, IPaymentForm>
{
    protected override async Task<IPaymentForm> HandleCoreAsync(IRequestInput input, RequestGetPaymentForm obj)
    {
        if (obj.Invoice is TInputInvoiceStars starsInvoice)
        {
            return await HandleStarsTopupAsync(input, starsInvoice);
        }

        if (obj.Invoice is TInputInvoiceStarGift starGiftInvoice)
        {
            var collection = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
            var gift = await collection.Find(d => d.GiftId == starGiftInvoice.GiftId).FirstOrDefaultAsync();
            if (gift == null)
                RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Gift", Amount = gift.Stars }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftUpgrade upgradeInvoice)
        {
            var savedCol = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            SavedStarGiftDocument? saved = upgradeInvoice.Stargift is TInputSavedStarGiftUser u
                ? await savedCol.Find(d => d.OwnerUserId == input.UserId && d.MessageId == u.MsgId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync()
                  ?? await savedCol.Find(d => d.OwnerUserId == input.UserId && !d.IsUnique && d.UpgradeStars.HasValue).FirstOrDefaultAsync()
                : null;
            if (saved == null || saved.IsUnique || !saved.UpgradeStars.HasValue)
                RpcErrors.RpcErrors400.StargiftUpgradeUnavailable.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Upgrade", Amount = saved!.UpgradeStars!.Value }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftPrepaidUpgrade prepaidForm)
        {
            var savedCol2 = mongoDatabase.GetCollection<SavedStarGiftDocument>("saved-star-gifts");
            var peer = peerHelper.GetPeer(prepaidForm.Peer, input.UserId)!;
            var saved2 = await savedCol2.Find(d =>
                d.OwnerUserId == peer.PeerId &&
                d.PrepaidUpgradeHash == prepaidForm.Hash &&
                !d.IsUnique).FirstOrDefaultAsync();
            if (saved2 == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Upgrade", Amount = 0 }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftDropOriginalDetails)
        {
            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Remove original details", Amount = 25 }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftAuctionBid auctionBid)
        {
            var col = mongoDatabase.GetCollection<AuctionDocument>("star-gift-auctions");
            var auction = await col.Find(x => x.GiftId == auctionBid.GiftId && !x.Finished).FirstOrDefaultAsync();
            if (auction == null) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
            if (auctionBid.BidAmount < auction!.MinBidAmount) RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = "Auction bid", Amount = auctionBid.BidAmount }],
                },
            };
        }

        if (obj.Invoice is TInputInvoiceStarGiftResale resaleForm)
        {
            var uniqueDoc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
                .Find(d => d.Slug == resaleForm.Slug && d.ResellStars > 0).FirstOrDefaultAsync();
            if (uniqueDoc == null) RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

            return new TPaymentFormStarGift
            {
                FormId = Random.Shared.NextInt64(),
                Invoice = new TInvoice
                {
                    Currency = "XTR",
                    Prices = [new TLabeledPrice { Label = uniqueDoc!.Title, Amount = uniqueDoc.ResellStars }],
                },
            };
        }

        RpcErrors.RpcErrors400.PaymentProviderInvalid.ThrowRpcError();
        return default!;
    }

    private async Task<TVector<IPaymentSavedCredentials>?> GetSavedCredentialsAsync(long userId)
    {
        var col = mongoDatabase.GetCollection<SavedPaymentCredentialDocument>("saved-payment-credentials");
        var cards = await col.Find(x => x.UserId == userId).ToListAsync();
        if (cards.Count == 0) return null;
        return new TVector<IPaymentSavedCredentials>(
            cards.Select(c => (IPaymentSavedCredentials)new TPaymentSavedCredentialsCard
            {
                Id = c.PaymentMethodId,
                Title = c.Title,
            }).ToList());
    }

    private async Task<IPaymentForm> HandleStarsTopupAsync(IRequestInput input, TInputInvoiceStars invoice)
    {
        var stripe = options.Value.Stripe;
        long amountCents = 0;
        long stars = 0;
        long recipientUserId = 0;

        if (invoice.Purpose is TInputStorePaymentStarsTopup topup)
        {
            stars = topup.Stars;
            // Find matching option or use provided amount
            var opt = StripeHelper.TopupOptions.FirstOrDefault(o => o.Stars == topup.Stars);
            amountCents = opt != default ? opt.Amount : topup.Amount;
        }
        else if (invoice.Purpose is TInputStorePaymentStarsGift gift)
        {
            stars = gift.Stars;
            var opt = StripeHelper.TopupOptions.FirstOrDefault(o => o.Stars == gift.Stars);
            amountCents = opt != default ? opt.Amount : gift.Amount;
            recipientUserId = gift.UserId is TInputUser inputUser ? inputUser.UserId : peerHelper.GetPeer(gift.UserId, input.UserId).PeerId;
        }

        string nativeParamsJson;
        long formId = Random.Shared.NextInt64();

        if (!string.IsNullOrEmpty(stripe.SecretKey))
        {
            var (clientSecret, paymentIntentId) = await StripeHelper.CreatePaymentIntentAsync(
                stripe.SecretKey, amountCents, "usd", $"Telegram Stars x{stars}");

            // Store mapping formId -> paymentIntentId for later verification
            await mongoDatabase.GetCollection<StripePaymentIntentDocument>("stripe-payment-intents")
                .InsertOneAsync(new StripePaymentIntentDocument
                {
                    FormId = formId,
                    PaymentIntentId = paymentIntentId,
                    ClientSecret = clientSecret,
                    UserId = input.UserId,
                    RecipientUserId = recipientUserId,
                    Stars = stars,
                    CreatedAt = DateTime.UtcNow,
                });

            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                payment_intent_client_secret = clientSecret,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }
        else
        {
            nativeParamsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                publishable_key = stripe.PublishableKey,
                need_country = false,
                need_zip = false,
                need_cardholder_name = false,
            });
        }

        return new TPaymentForm
        {
            FormId = formId,
            BotId = MyTelegramConsts.NotificationServiceUserId,
            Title = $"Buy {stars} Telegram Stars",
            Description = $"Top up your Telegram Stars balance with {stars} stars",
            NativeProvider = "stripe",
            NativeParams = new TDataJSON { Data = nativeParamsJson },
            ProviderId = 0,
            Url = string.Empty,
            Invoice = new TInvoice
            {
                Currency = "USD",
                Prices = [new TLabeledPrice { Label = $"{stars} Telegram Stars", Amount = amountCents }],
            },
            SavedCredentials = await GetSavedCredentialsAsync(input.UserId),
            Users = [],
        };
    }
}
