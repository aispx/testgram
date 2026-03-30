using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.StarGifts;

public static class AuctionHelper
{
    public static IStarGiftAuctionState ToState(AuctionDocument doc)
    {
        if (doc == null)
        {
            return new TStarGiftAuctionState
            {
                Version = 1,
                StartDate = 0,
                EndDate = 0,
                MinBidAmount = 0,
                BidLevels = new TVector<IAuctionBidLevel>(),
                TopBidders = new TVector<long>(),
                NextRoundAt = 0,
                LastGiftNum = 0,
                GiftsLeft = 0,
                CurrentRound = 0,
                TotalRounds = 0,
                Rounds = new TVector<IStarGiftAuctionRound>(),
            };
        }

        if (doc.Finished || doc.EndDate <= 0)
        {
            return new TStarGiftAuctionStateFinished
            {
                StartDate = doc.StartDate,
                EndDate = doc.EndDate,
                AveragePrice = doc.AveragePrice,
            };
        }

        var bids = doc.Bids ?? [];
        
        var activeBids = bids
            .Where(b => !b.Returned)
            .GroupBy(b => b.UserId)
            .Select(g => g.OrderByDescending(b => b.Amount).First())
            .OrderByDescending(b => b.Amount)
            .ToList();

        var bidLevels = activeBids
            .Select((b, i) => (IAuctionBidLevel)new TAuctionBidLevel
            {
                Pos = i + 1,
                Amount = b.Amount,
                Date = b.Date,
            })
            .ToList();

        var topBidders = activeBids.Take(Math.Min(3, activeBids.Count)).Select(b => b.UserId).ToList();

        int distributed = (doc.AvailabilityTotal ?? (doc.GiftsPerRound > 0 ? doc.GiftsPerRound * doc.TotalRounds : 0)) - doc.GiftsLeft;
        int lastGiftNum = distributed > 0 ? distributed : 0;

        var rounds = new List<IStarGiftAuctionRound>();
        foreach (var config in doc.RoundConfigs ?? [])
        {
            if (config.RoundNumber <= doc.CurrentRound)
            {
                continue;
            }
            IStarGiftAuctionRound round;
            if (config.ExtendTopCount > 0)
            {
                round = new TStarGiftAuctionRoundExtendable
                {
                    Num = config.RoundNumber,
                    Duration = config.DurationSeconds,
                    ExtendTop = config.ExtendTopCount,
                    ExtendWindow = config.ExtendWindowSeconds,
                };
            }
            else
            {
                round = new TStarGiftAuctionRound
                {
                    Num = config.RoundNumber,
                    Duration = config.DurationSeconds,
                };
            }
            rounds.Add(round);
        }

        if (rounds.Count == 0)
        {
            rounds.Add(new TStarGiftAuctionRound
            {
                Num = doc.CurrentRound + 1,
                Duration = doc.RoundDurationSeconds,
            });
        }

        return new TStarGiftAuctionState
        {
            Version = doc.Version > 0 ? doc.Version : 1,
            StartDate = doc.StartDate,
            EndDate = doc.EndDate,
            MinBidAmount = doc.MinBidAmount,
            BidLevels = new TVector<IAuctionBidLevel>(bidLevels),
            TopBidders = new TVector<long>(topBidders),
            NextRoundAt = doc.NextRoundAt,
            LastGiftNum = lastGiftNum,
            GiftsLeft = doc.GiftsLeft,
            CurrentRound = doc.CurrentRound > 0 ? doc.CurrentRound : 1,
            TotalRounds = doc.TotalRounds > 0 ? doc.TotalRounds : 1,
            Rounds = new TVector<IStarGiftAuctionRound>(rounds),
        };
    }

    public static TStarGiftAuctionUserState ToUserState(AuctionDocument doc, long userId, int acquiredCount = 0)
    {
        if (doc == null)
            return new TStarGiftAuctionUserState { AcquiredCount = 0 };

        var bids = doc.Bids ?? [];
        
        var bid = bids
            .Where(b => b.UserId == userId && !b.Returned)
            .OrderByDescending(b => b.Amount)
            .FirstOrDefault();

        if (bid == null)
            return new TStarGiftAuctionUserState { AcquiredCount = acquiredCount };

        var activeBids = bids
            .Where(b => !b.Returned)
            .GroupBy(b => b.UserId)
            .Select(g => g.OrderByDescending(b => b.Amount).First())
            .OrderByDescending(b => b.Amount)
            .ToList();

        long minBid = activeBids.Count >= doc.GiftsPerRound
            ? (activeBids.ElementAtOrDefault(doc.GiftsPerRound - 1)?.Amount ?? doc.MinBidAmount) + 1
            : doc.MinBidAmount;

        return new TStarGiftAuctionUserState
        {
            BidAmount = bid.Amount,
            BidDate = bid.Date,
            MinBidAmount = minBid,
            BidPeer = new TPeerUser { UserId = userId },
            AcquiredCount = acquiredCount,
        };
    }

    public static async Task<AuctionDocument?> FindAsync(IMongoDatabase db, IInputStarGiftAuction input)
    {
        var col = db.GetCollection<AuctionDocument>("star-gift-auctions");
        return input switch
        {
            TInputStarGiftAuction a => await col.Find(x => x.GiftId == a.GiftId).FirstOrDefaultAsync(),
            TInputStarGiftAuctionSlug s => await col.Find(x => x.Slug == s.Slug).FirstOrDefaultAsync(),
            _ => null
        };
    }
}
