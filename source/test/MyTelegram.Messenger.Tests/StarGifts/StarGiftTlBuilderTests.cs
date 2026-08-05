using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.Messenger.Tests.StarGifts;

/// <summary>
/// Feature: star gifts — serializing a non-upgraded gift.
///
/// <para>
/// <c>starGift#313a9547</c> reuses single flag bits for several fields: bit 0 marks <c>limited</c>
/// and also guards <c>availability_remains</c> / <c>availability_total</c>; bit 1 marks
/// <c>sold_out</c> and guards <c>first_sale_date</c> / <c>last_sale_date</c>. The generated
/// serializer sets a bit from the boolean but only writes the numbers when they have values, while
/// the deserializer reads them whenever the bit is set. A gift flagged limited without availability
/// counts therefore emits a stream that is read at the wrong offset from that point on — every
/// later field, including the sticker and the price, becomes garbage. These tests hold the
/// normalization that keeps flag and payload in agreement.
/// </para>
/// </summary>
public class StarGiftTlBuilderTests
{
    private static IDocument Sticker() => new TDocumentEmpty { Id = 3000010 };

    private static TStarGift Build(StarGiftDocument? meta) =>
        StarGiftTlBuilder.Build(5, 250, 200, 1000, Sticker(), meta);

    [Fact]
    public void A_limited_gift_without_availability_counts_round_trips()
    {
        // The shape incomplete metadata produces, and the one that used to corrupt the stream.
        var gift = Build(new StarGiftDocument { GiftId = 5, Limited = true });

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();

        rt.Id.ShouldBe(5L);
        rt.Stars.ShouldBe(250L);
        rt.ConvertStars.ShouldBe(200L);
        rt.UpgradeStars.ShouldBe(1000L);
    }

    [Fact]
    public void A_limited_gift_without_availability_counts_does_not_claim_the_flag()
    {
        var gift = Build(new StarGiftDocument { GiftId = 5, Limited = true });

        // Dropping the flag is what keeps the payload readable; the alternative would be inventing
        // availability numbers the server does not have.
        gift.Limited.ShouldBeFalse();
        gift.AvailabilityRemains.ShouldBeNull();
        gift.AvailabilityTotal.ShouldBeNull();
    }

    [Fact]
    public void A_partially_specified_availability_pair_is_dropped_whole()
    {
        // Bit 0 guards both ints, so writing one without the other desynchronizes the reader.
        var gift = Build(new StarGiftDocument { GiftId = 5, Limited = true, AvailabilityTotal = 100 });

        gift.Limited.ShouldBeFalse();
        gift.AvailabilityTotal.ShouldBeNull();

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();
        rt.ConvertStars.ShouldBe(200L);
    }

    [Fact]
    public void A_genuinely_limited_gift_keeps_its_flag_and_counts()
    {
        var gift = Build(new StarGiftDocument
        {
            GiftId = 5, Limited = true, AvailabilityRemains = 7, AvailabilityTotal = 100
        });

        gift.Limited.ShouldBeTrue();

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();
        rt.Limited.ShouldBeTrue();
        rt.AvailabilityRemains.ShouldBe(7);
        rt.AvailabilityTotal.ShouldBe(100);
        rt.ConvertStars.ShouldBe(200L);
    }

    [Fact]
    public void A_sold_out_gift_without_sale_dates_round_trips()
    {
        // Bit 1 has the same shape as bit 0, one field pair further along.
        var gift = Build(new StarGiftDocument { GiftId = 5, SoldOut = true });

        gift.SoldOut.ShouldBeFalse();

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();
        rt.Stars.ShouldBe(250L);
        rt.ConvertStars.ShouldBe(200L);
        rt.UpgradeStars.ShouldBe(1000L);
    }

    [Fact]
    public void A_genuinely_sold_out_gift_keeps_its_flag_and_dates()
    {
        var gift = Build(new StarGiftDocument
        {
            GiftId = 5, SoldOut = true, FirstSaleDate = 1712160000, LastSaleDate = 1778854278
        });

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();

        rt.SoldOut.ShouldBeTrue();
        rt.FirstSaleDate.ShouldBe(1712160000);
        rt.LastSaleDate.ShouldBe(1778854278);
        rt.UpgradeStars.ShouldBe(1000L);
    }

    [Fact]
    public void A_gift_whose_type_metadata_is_missing_still_round_trips()
    {
        // Live data has saved gifts referencing a GiftId absent from `star-gifts`.
        var gift = Build(null);

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();

        rt.Id.ShouldBe(5L);
        rt.Stars.ShouldBe(250L);
        rt.ConvertStars.ShouldBe(200L);
        rt.UpgradeStars.ShouldBe(1000L);
        rt.Limited.ShouldBeFalse();
        rt.SoldOut.ShouldBeFalse();
    }

    [Fact]
    public void The_title_from_metadata_survives_the_round_trip()
    {
        var gift = Build(new StarGiftDocument { GiftId = 5, Title = "Klitor" });

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();

        rt.Title.ShouldBe("Klitor");
        rt.ConvertStars.ShouldBe(200L);
    }

    [Fact]
    public void Both_shared_flags_being_incomplete_at_once_still_round_trips()
    {
        var gift = Build(new StarGiftDocument
        {
            GiftId = 5, Limited = true, SoldOut = true, Title = "Klitor"
        });

        var rt = gift.ToBytes()!.AsMemory().ToTObject<TStarGift>();

        rt.Title.ShouldBe("Klitor");
        rt.Stars.ShouldBe(250L);
        rt.ConvertStars.ShouldBe(200L);
        rt.UpgradeStars.ShouldBe(1000L);
    }
}
