using MongoDB.Bson;
using MyTelegram.Messenger.Services.Stickers;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: the <c>Attributes2</c> field of a document read model round-trips.
///
/// <para>Sticker set edits rewrite the sticker classification of a document — the emoji, the mask
/// coordinates, which set it belongs to — and every read path deserializes that field back into
/// <c>documentAttributeSticker</c> / <c>documentAttributeCustomEmoji</c>. If a write produces a shape the
/// read cannot parse, the failure is silent: the deserializer's catch block hands back an empty list, the
/// mapper substitutes generic attributes, and the sticker quietly loses its emoji and its link to its
/// pack.</para>
/// </summary>
public class StickerAttributeSerializerTests
{
    public StickerAttributeSerializerTests()
    {
        // Both directions need the discriminator conventions the servers register at startup: without
        // them a read silently yields nothing and a write is rejected outright.
        MongoDbTestSerializers.EnsureRegistered();
    }

    /// <summary>
    /// The shape <c>scripts/seed_stickers.py</c> writes: an array of documents each tagged with a short
    /// <c>_t</c> type name. Used as an oracle so a change in driver conventions cannot pass unnoticed.
    /// </summary>
    private static BsonDocument SeededRow()
    {
        return new BsonDocument
        {
            ["DocumentId"] = 1234L,
            ["Attributes2"] = new BsonArray
            {
                new BsonDocument
                {
                    ["_t"] = "TDocumentAttributeSticker",
                    ["Alt"] = "🐱",
                    ["Mask"] = false,
                    ["Stickerset"] = new BsonDocument
                    {
                        ["_t"] = "TInputStickerSetID",
                        ["Id"] = 99L,
                        ["AccessHash"] = 77L
                    }
                },
                new BsonDocument
                {
                    ["_t"] = "TDocumentAttributeImageSize",
                    ["W"] = 512,
                    ["H"] = 512
                }
            }
        };
    }

    [Fact]
    public void Reads_the_shape_the_seeder_writes()
    {
        var attributes = StickerAttributeSerializer.Read(SeededRow());

        attributes.Count.ShouldBe(2);
        var sticker = attributes.OfType<TDocumentAttributeSticker>().ShouldHaveSingleItem();
        sticker.Alt.ShouldBe("🐱");
        // The seeders wrote the nested id as "Id" while the driver's automapper expects "_id"; a sticker
        // read back with stickerset id 0 is one whose pack cannot be opened from a chat.
        ((TInputStickerSetID)sticker.Stickerset).Id.ShouldBe(99);
    }

    /// <summary>
    /// The driver's own shape, with the nested id under <c>_id</c>, has to keep working — it is what every
    /// row written by this server looks like.
    /// </summary>
    [Fact]
    public void Reads_the_shape_the_driver_writes()
    {
        var row = SeededRow();
        var stickerset = row["Attributes2"].AsBsonArray[0].AsBsonDocument["Stickerset"].AsBsonDocument;
        stickerset.Remove("Id");
        stickerset["_id"] = 99L;

        var sticker = StickerAttributeSerializer.Read(row)
            .OfType<TDocumentAttributeSticker>()
            .ShouldHaveSingleItem();

        ((TInputStickerSetID)sticker.Stickerset).Id.ShouldBe(99);
    }

    [Fact]
    public void Writes_a_shape_it_can_read_back()
    {
        var written = StickerAttributeSerializer.Write([
            new TDocumentAttributeSticker
            {
                Alt = "🐶",
                Mask = true,
                MaskCoords = new TMaskCoords { N = 1, X = 0.5, Y = 0.25, Zoom = 2 },
                Stickerset = new TInputStickerSetID { Id = 5, AccessHash = 6 }
            },
            new TDocumentAttributeImageSize { W = 512, H = 512 }
        ]);

        var reread = StickerAttributeSerializer.Read(new BsonDocument { ["Attributes2"] = written });

        reread.Count.ShouldBe(2);
        var sticker = reread.OfType<TDocumentAttributeSticker>().ShouldHaveSingleItem();
        sticker.Alt.ShouldBe("🐶");
        sticker.Mask.ShouldBeTrue();
        // mask_coords is what positions a mask on a face; losing it puts every mask on the forehead.
        sticker.MaskCoords.ShouldNotBeNull();
        ((TMaskCoords)sticker.MaskCoords!).N.ShouldBe(1);
        ((TInputStickerSetID)sticker.Stickerset).Id.ShouldBe(5);
    }

    [Fact]
    public void Replacing_the_primary_attribute_keeps_the_others()
    {
        var written = StickerAttributeSerializer.WithPrimaryAttribute(SeededRow(),
            new TDocumentAttributeCustomEmoji
            {
                Alt = "🎉",
                Free = true,
                Stickerset = new TInputStickerSetID { Id = 1, AccessHash = 2 }
            });

        var reread = StickerAttributeSerializer.Read(new BsonDocument { ["Attributes2"] = written });

        // The stale sticker classification is gone, the image size survives: a document moved into a
        // custom-emoji set must not keep claiming to be a plain sticker, and must not lose its dimensions.
        reread.OfType<TDocumentAttributeSticker>().ShouldBeEmpty();
        reread.OfType<TDocumentAttributeCustomEmoji>().ShouldHaveSingleItem().Alt.ShouldBe("🎉");
        reread.OfType<TDocumentAttributeImageSize>().ShouldHaveSingleItem().W.ShouldBe(512);
    }
}
