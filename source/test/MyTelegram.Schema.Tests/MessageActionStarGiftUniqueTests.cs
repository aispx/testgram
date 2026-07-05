namespace MyTelegram.Schema;

public class MessageActionStarGiftUniqueTests
{
    [Fact]
    public void Serialize_WithPeerAndResaleAmount_WritesSavedIdPlaceholderBeforeResaleAmount()
    {
        var action = new TMessageActionStarGiftUnique
        {
            Gift = new TStarGift
            {
                Id = 1,
                Sticker = new TDocumentEmpty { Id = 2 },
                Stars = 10,
                ConvertStars = 5,
            },
            Transferred = true,
            FromId = new TPeerUser { UserId = 1001 },
            Peer = new TPeerUser { UserId = 1002 },
            ResaleAmount = new TStarsAmount { Amount = 123, Nanos = 0 },
        };

        var bytes = action.ToBytes().ShouldNotBeNull();

        var roundTripped = bytes.AsMemory().ToTObject<TMessageActionStarGiftUnique>();

        roundTripped.Transferred.ShouldBeTrue();
        roundTripped.FromId.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(1001);
        roundTripped.Peer.ShouldBeOfType<TPeerUser>().UserId.ShouldBe(1002);
        roundTripped.SavedId.ShouldBe(0);
        roundTripped.ResaleAmount.ShouldBeOfType<TStarsAmount>().Amount.ShouldBe(123);
    }
}
