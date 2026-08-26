using MongoDB.Bson;
using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: <c>stickerSet.hash</c>, the token that lets a client keep its cached copy of a sticker set.
///
/// <para>
/// A client quotes the hash back in <c>messages.getStickerSet.hash</c> and expects
/// <c>messages.stickerSetNotModified</c> when nothing changed. The value is the server's to define, but
/// three properties are not negotiable. It must never be zero, because that is the client's "nothing
/// cached" sentinel (Android's <c>MediaDataController.processLoadedStickers</c> re-requests a set whose
/// hash is zero on every poll). It must not depend on anything that varies between sessions of the same
/// user — the documents' access hashes are minted per session, so a hash derived from them would never
/// match again after a reconnect. And every method that returns the set has to arrive at the same number,
/// because a client hashes its installed list from the per-set hashes it cached last
/// (<c>calcStickersHash</c>), whichever response they came from.
/// </para>
/// </summary>
public class StickerSetHashHelperTests
{
    private const long SetId = 1258816259751983;
    private const string ShortName = "AnimatedEmojies";
    private const long Version = 1;

    private static readonly (long DocumentId, string? Alt)[] Documents =
    [
        (5181593617004757506, "👍"),
        (5130017893072765649, "😂"),
        (5181852277115192162, "👎")
    ];

    [Fact]
    public void The_same_contents_hash_the_same()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents)
            .ShouldBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents));
    }

    /// <summary>
    /// Zero is the client's "no cached copy" value, so the server must never produce it — otherwise the
    /// set is re-downloaded on every poll and, worse, a request carrying a zero hash would be answered
    /// with notModified against an empty cache.
    /// </summary>
    [Fact]
    public void The_hash_is_never_zero()
    {
        StickerSetHashHelper.ComputeHash(0, null, 0, 0, []).ShouldNotBe(0);
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents).ShouldNotBe(0);
    }

    /// <summary>
    /// stickerSet.hash is an int and clients compare it as one; a negative value would still round-trip,
    /// but keeping it positive matches what the official server sends and avoids sign-conversion
    /// surprises in clients that widen it to a long.
    /// </summary>
    [Fact]
    public void The_hash_is_positive()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents).ShouldBePositive();
    }

    [Fact]
    public void A_document_added_or_removed_changes_the_hash()
    {
        var baseline = StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents);

        StickerSetHashHelper.ComputeHash(SetId, ShortName, 2, Version, Documents.Take(2))
            .ShouldNotBe(baseline);
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 4, Version,
                Documents.Append((5127515421787817008, "🥳")))
            .ShouldNotBe(baseline);
    }

    /// <summary>
    /// Order is part of the identity: clients render the set in the order they receive it, so a reorder
    /// is a visible change and must invalidate the cached copy.
    /// </summary>
    [Fact]
    public void Reordering_the_documents_changes_the_hash()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents.Reverse())
            .ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents));
    }

    /// <summary>
    /// The alt is what the client matches an emoji against — <c>getEmojiAnimatedSticker</c> looks a GIF
    /// search tab up by it — so a change to which emoji a sticker answers to has to reach clients rather
    /// than sitting behind notModified forever. The alt fed in here is the emoji of the set's own
    /// <c>stickerPack</c> entry, which is the only form available to the list methods.
    /// </summary>
    [Fact]
    public void Changing_an_alt_changes_the_hash()
    {
        var corrected = Documents.ToArray();
        corrected[0] = (corrected[0].DocumentId, "👍️");

        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, corrected)
            .ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents));
    }

    /// <summary>
    /// The revision covers every edit the document ids and pack emoji cannot express — a re-seed that only
    /// rewrites per-document alts, a renamed title, a new thumbnail. Without it such a change would sit
    /// behind notModified until the set's contents happened to change too.
    /// </summary>
    [Fact]
    public void A_new_revision_changes_the_hash()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version + 1, Documents)
            .ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents));
    }

    [Fact]
    public void Different_sets_with_identical_documents_hash_differently()
    {
        var byId = StickerSetHashHelper.ComputeHash(SetId + 1, ShortName, 3, Version, Documents);
        var byShortName = StickerSetHashHelper.ComputeHash(SetId, "SomethingElse", 3, Version, Documents);
        var baseline = StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents);

        byId.ShouldNotBe(baseline);
        byShortName.ShouldNotBe(baseline);
    }

    /// <summary>
    /// A missing alt is normal for plain stickers, and must not throw or collapse two different
    /// documents onto one hash.
    /// </summary>
    [Fact]
    public void Documents_without_an_alt_are_hashed()
    {
        var withoutAlt = StickerSetHashHelper.ComputeHash(SetId, ShortName, 2, Version,
            [(1L, null), (2L, null)]);

        withoutAlt.ShouldNotBe(0);
        withoutAlt.ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 2, Version,
            [(2L, null), (1L, null)]));
    }

    /// <summary>
    /// The catalogue overload is the one every caller uses, and it has to agree with the explicit form:
    /// <c>messages.getStickerSet</c> and <c>messages.getAllStickers</c> both go through it, and a client
    /// that cached the set from one response hashes its installed list from that cached value.
    /// </summary>
    [Fact]
    public void The_catalogue_overload_matches_the_explicit_one()
    {
        var row = new BsonDocument
        {
            ["StickerSetId"] = SetId,
            ["ShortName"] = ShortName,
            ["Count"] = 3,
            ["Version"] = Version,
            ["DocumentIds"] = new BsonArray(Documents.Select(p => p.DocumentId).ToArray()),
            ["Packs"] = new BsonArray(Documents.Select(p => new BsonDocument
            {
                ["Emoticon"] = p.Alt,
                ["Documents"] = new BsonArray(new[] { p.DocumentId })
            }))
        };

        StickerSetHashHelper.ComputeHash(row)
            .ShouldBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Version, Documents));
    }

    /// <summary>
    /// Seeded rows keep the short name in <c>Slug</c> and carry no <c>ShortName</c> at all. Reading the
    /// wrong one used to throw; hashing the empty string instead would make two different sets collide.
    /// </summary>
    [Fact]
    public void A_row_with_only_a_slug_hashes_by_that_slug()
    {
        var row = new BsonDocument
        {
            ["StickerSetId"] = SetId,
            ["Slug"] = ShortName,
            ["Count"] = 0,
            ["Version"] = Version,
            ["DocumentIds"] = new BsonArray()
        };

        StickerSetHashHelper.ComputeHash(row)
            .ShouldBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 0, Version, []));
    }
}
