using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetStarGiftAuctionAcquiredGiftsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestGetStarGiftAuctionAcquiredGifts, MyTelegram.Schema.Payments.IStarGiftAuctionAcquiredGifts>, IObjectHandler
{
    protected override async Task<MyTelegram.Schema.Payments.IStarGiftAuctionAcquiredGifts> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestGetStarGiftAuctionAcquiredGifts obj)
    {
        var col = mongoDatabase.GetCollection<AuctionAcquiredGiftDocument>("star-gift-auction-acquired");
        var docs = await col.Find(x => x.GiftId == obj.GiftId && x.UserId == input.UserId).ToListAsync();

        var gifts = docs.Select(d => (IStarGiftAuctionAcquiredGift)new TStarGiftAuctionAcquiredGift
        {
            Peer = new TPeerUser { UserId = d.UserId },
            Date = d.Date,
            BidAmount = d.BidAmount,
            Round = d.Round,
            Pos = d.Pos,
            NameHidden = d.NameHidden,
            Message = d.MessageText != null ? new TTextWithEntities { Text = d.MessageText, Entities = [] } : null,
            GiftNum = d.GiftNum,
        }).ToList();

        return new MyTelegram.Schema.Payments.TStarGiftAuctionAcquiredGifts
        {
            Gifts = new TVector<IStarGiftAuctionAcquiredGift>(gifts),
            Users = [],
            Chats = [],
        };
    }
}
