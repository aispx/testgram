using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

public class AuctionAcquiredGiftDocument
{
    [BsonId]
    public ObjectId Id { get; set; }
    public long GiftId { get; set; }
    public long UserId { get; set; }
    public int Date { get; set; }
    public long BidAmount { get; set; }
    public int Round { get; set; }
    public int Pos { get; set; }
    public bool NameHidden { get; set; }
    public string? MessageText { get; set; }
    public int? GiftNum { get; set; }
}
