using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: <c>stickerSet.hash</c>, the token that lets a client keep its cached copy of a sticker set.
///
/// <para>
/// A client quotes the hash back in <c>messages.getStickerSet.hash</c> and expects
/// <c>messages.stickerSetNotModified</c> when nothing changed. The value is the server's to define, but
/// two properties are not negotiable: it must never be zero, because that is the client's "nothing
/// cached" sentinel (Android's <c>MediaDataController.processLoadedStickers</c> re-requests a set whose
/// hash is zero on every poll), and it must not depend on anything that varies between sessions of the
/// same user — the documents' access hashes are minted per session, so a hash derived from them would
/// never match again after a reconnect.
/// </para>
/// </summary>
public class StickerSetHashHelperTests
{
    private const long SetId = 1258816259751983;
    private const string ShortName = "AnimatedEmojies";

    private static readonly (long DocumentId, string? Alt)[] Documents =
    [
        (5181593617004757506, "👍"),
        (5130017893072765649, "😂"),
        (5181852277115192162, "👎")
    ];

    [Fact]
    public void The_same_contents_hash_the_same()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents)
            .ShouldBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents));
    }

    /// <summary>
    /// Zero is the client's "no cached copy" value, so the server must never produce it — otherwise the
    /// set is re-downloaded on every poll and, worse, a request carrying a zero hash would be answered
    /// with notModified against an empty cache.
    /// </summary>
    [Fact]
    public void The_hash_is_never_zero()
    {
        StickerSetHashHelper.ComputeHash(0, null, 0, []).ShouldNotBe(0);
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents).ShouldNotBe(0);
    }

    /// <summary>
    /// stickerSet.hash is an int and clients compare it as one; a negative value would still round-trip,
    /// but keeping it positive matches what the official server sends and avoids sign-conversion
    /// surprises in clients that widen it to a long.
    /// </summary>
    [Fact]
    public void The_hash_is_positive()
    {
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents).ShouldBePositive();
    }

    [Fact]
    public void A_document_added_or_removed_changes_the_hash()
    {
        var baseline = StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents);

        StickerSetHashHelper.ComputeHash(SetId, ShortName, 2, Documents.Take(2))
            .ShouldNotBe(baseline);
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 4,
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
        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents.Reverse())
            .ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents));
    }

    /// <summary>
    /// The alt is what the client matches an emoji against — <c>getEmojiAnimatedSticker</c> looks a GIF
    /// search tab up by it — so a re-seed that corrects an alt has to reach clients rather than sitting
    /// behind notModified forever.
    /// </summary>
    [Fact]
    public void Changing_an_alt_changes_the_hash()
    {
        var corrected = Documents.ToArray();
        corrected[0] = (corrected[0].DocumentId, "👍️");

        StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, corrected)
            .ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents));
    }

    [Fact]
    public void Different_sets_with_identical_documents_hash_differently()
    {
        var byId = StickerSetHashHelper.ComputeHash(SetId + 1, ShortName, 3, Documents);
        var byShortName = StickerSetHashHelper.ComputeHash(SetId, "SomethingElse", 3, Documents);
        var baseline = StickerSetHashHelper.ComputeHash(SetId, ShortName, 3, Documents);

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
        var withoutAlt = StickerSetHashHelper.ComputeHash(SetId, ShortName, 2,
            [(1L, null), (2L, null)]);

        withoutAlt.ShouldNotBe(0);
        withoutAlt.ShouldNotBe(StickerSetHashHelper.ComputeHash(SetId, ShortName, 2,
            [(2L, null), (1L, null)]));
    }
}
