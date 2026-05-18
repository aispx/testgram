using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Check if the specified <a href="https://corefork.telegram.org/api/gifts">gift »</a> can be sent.
/// Possible errors
/// Code Type Description
/// 400 STARGIFT_INVALID The passed gift is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.checkCanSendGift"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckCanSendGiftHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCheckCanSendGift, MyTelegram.Schema.Payments.ICheckCanSendGiftResult>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.ICheckCanSendGiftResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCheckCanSendGift obj)
    {
        // Mirrors the validation the client would otherwise hit when calling
        // payments.sendStarsForm with TInputInvoiceStarGift, but without taking
        // any star balance. We surface the same outcomes as the official server:
        //  - STARGIFT_INVALID when the gift is unknown,
        //  - sold-out / locked / not-yet-released gifts return a Fail with reason,
        //  - everything else returns Ok.
        var giftCol = mongoDatabase.GetCollection<StarGiftDocument>("star-gifts");
        var gift = await giftCol.Find(g => g.GiftId == obj.GiftId).FirstOrDefaultAsync();
        if (gift == null)
        {
            RpcErrors.RpcErrors400.StargiftInvalid.ThrowRpcError();
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (gift!.LockedUntilDate.HasValue && gift.LockedUntilDate.Value > now)
        {
            return Fail("This gift is not available yet.");
        }

        if (gift.SoldOut || (gift.Limited && gift.AvailabilityRemains is <= 0))
        {
            return Fail("This gift is sold out.");
        }

        if (gift.LimitedPerUser && gift.PerUserRemains is <= 0)
        {
            return Fail("You have reached the per-user limit for this gift.");
        }

        if (gift.IsAuction && gift.AuctionStartDate is { } start && start > now)
        {
            return Fail("This auction has not started yet.");
        }

        return new TCheckCanSendGiftResultOk();
    }

    private static TCheckCanSendGiftResultFail Fail(string reason)
    {
        return new TCheckCanSendGiftResultFail
        {
            Reason = new TTextWithEntities
            {
                Text = reason,
                Entities = new TVector<IMessageEntity>(),
            }
        };
    }
}