using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Stored in MongoDB collection "star-gift-offers"
/// </summary>
public class StarGiftOfferDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public long SenderUserId { get; set; }   // user who sent the offer
    public long RecipientUserId { get; set; } // user who received the offer
    public string Slug { get; set; } = "";    // unique gift slug
    public long PriceAmount { get; set; }     // offered price in stars
    public int ExpiresAt { get; set; }        // unix timestamp
    public long RandomId { get; set; }        // random_id used when sending the message
    public int MessageId { get; set; }        // message id of the offer message
    public bool Resolved { get; set; }        // accepted or declined
}
