using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class AuctionDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long GiftId { get; set; }
    public string Slug { get; set; } = "";
    public int Version { get; set; } = 1;
    public int StartDate { get; set; }
    public int EndDate { get; set; }
    public long MinBidAmount { get; set; }
    public int GiftsPerRound { get; set; } = 1;
    public int TotalRounds { get; set; } = 1;
    public int CurrentRound { get; set; } = 1;
    public int GiftsLeft { get; set; }
    public int? AvailabilityTotal { get; set; }
    public int NextRoundAt { get; set; }
    public int RoundDurationSeconds { get; set; } = 300;
    public int RoundExtendWindowSeconds { get; set; } = 30;
    public int RoundExtendTopCount { get; set; } = 3;
    public bool Finished { get; set; }
    public long AveragePrice { get; set; }
    public int AveragePriceCount { get; set; }
    public List<AuctionBid> Bids { get; set; } = [];
    public List<AuctionRoundConfig> RoundConfigs { get; set; } = [];
}

public class AuctionBid
{
    public long UserId { get; set; }
    public long Amount { get; set; }
    public int Date { get; set; }
    public bool NameHidden { get; set; }
    public string? MessageText { get; set; }
    public long? PeerUserId { get; set; }
    public long? PeerChannelId { get; set; }
    public bool Returned { get; set; }
    public int RoundNumber { get; set; }
    public bool WonGift { get; set; }
    public long? WonGiftNum { get; set; }
}

public class AuctionRoundConfig
{
    public int RoundNumber { get; set; }
    public int DurationSeconds { get; set; }
    public int ExtendWindowSeconds { get; set; }
    public int ExtendTopCount { get; set; }
}
