using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: <c>messages.featuredStickers.hash</c>, which decides whether the trending page is re-downloaded.
///
/// <para>
/// Unlike the hashes the server is free to define, this one has to reproduce the client's own computation
/// exactly, because the client sends the hash of <b>its</b> cached list and the server can only answer
/// <c>featuredStickersNotModified</c> when the two agree. Both reference implementations fold in each set id
/// and then an extra <c>1</c> for every set still unread: Telegram Android
/// <c>MediaDataController.calcFeaturedStickersHash</c> and tdlib
/// <c>StickersManager::get_featured_sticker_sets_hash</c>.
/// </para>
/// </summary>
public class FeaturedStickerSetHashHelperTests
{
    private static readonly long[] SetIds = [1258816259751983, 1258816259751984, 1258816259751985];

    /// <summary>
    /// The client algorithm, transcribed from Android (<c>hash ^= hash &gt;&gt;&gt; 21</c> — Java's unsigned
    /// shift), used here as an independent oracle.
    /// </summary>
    private static long ReferenceHash(IEnumerable<long> setIds, ISet<long> unread)
    {
        var hash = 0UL;

        foreach (var setId in setIds)
        {
            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += (ulong)setId;

            if (!unread.Contains(setId))
            {
                continue;
            }

            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += 1;
        }

        return (long)hash;
    }

    [Fact]
    public void Matches_the_client_with_everything_read()
    {
        FeaturedStickerSetHashHelper.ComputeHash(SetIds, new HashSet<long>())
            .ShouldBe(ReferenceHash(SetIds, new HashSet<long>()));
    }

    [Fact]
    public void Matches_the_client_with_everything_unread()
    {
        var unread = SetIds.ToHashSet();

        FeaturedStickerSetHashHelper.ComputeHash(SetIds, unread)
            .ShouldBe(ReferenceHash(SetIds, unread));
    }

    /// <summary>
    /// The badge is part of the hash, so marking a set read has to change it — otherwise the client keeps
    /// the unread dot until something else about the list changes.
    /// </summary>
    [Fact]
    public void Reading_a_set_changes_the_hash()
    {
        var allUnread = FeaturedStickerSetHashHelper.ComputeHash(SetIds, SetIds.ToHashSet());
        var oneRead = FeaturedStickerSetHashHelper.ComputeHash(SetIds, SetIds.Skip(1).ToHashSet());

        oneRead.ShouldNotBe(allUnread);
        oneRead.ShouldBe(ReferenceHash(SetIds, SetIds.Skip(1).ToHashSet()));
    }

    [Fact]
    public void An_empty_list_hashes_to_zero()
    {
        // Zero is also what a client with nothing cached sends, so the two agree by construction and an
        // empty trending page is never answered notModified against a populated cache.
        FeaturedStickerSetHashHelper.ComputeHash([], new HashSet<long>()).ShouldBe(0);
    }

    [Fact]
    public void Reordering_changes_the_hash()
    {
        FeaturedStickerSetHashHelper.ComputeHash(SetIds.Reverse(), new HashSet<long>())
            .ShouldNotBe(FeaturedStickerSetHashHelper.ComputeHash(SetIds, new HashSet<long>()));
    }

    /// <summary>
    /// A signed right shift is what a C# <c>long</c> accumulator does by default, and it disagrees with every
    /// client once the accumulator goes negative. Stickerset ids are small enough that the two happen to
    /// coincide, so the ids here are deliberately large: the helper is shared with the document-id hashes,
    /// where real ids do drive the accumulator negative on the first element.
    /// </summary>
    [Fact]
    public void A_signed_accumulator_would_disagree()
    {
        long[] largeIds = [5181593617004757506, 5130017893072765649, 5181852277115192162];

        var signed = 0L;
        foreach (var id in largeIds)
        {
            signed ^= signed >> 21;
            signed ^= signed << 35;
            signed ^= signed >> 4;
            signed += id;
        }

        signed.ShouldNotBe(FeaturedStickerSetHashHelper.ComputeHash(largeIds, new HashSet<long>()));
        FeaturedStickerSetHashHelper.ComputeHash(largeIds, new HashSet<long>())
            .ShouldBe(ReferenceHash(largeIds, new HashSet<long>()));
    }
}
