using MyTelegram.Messenger.Services.Emoji;

namespace MyTelegram.Messenger.Tests.EmojiCategories;

/// <summary>
/// Feature: <c>emojiList.hash</c>, the token that lets a client keep its cached copy of the curated
/// custom-emoji lists behind the profile-photo, group-photo and accent-colour pickers.
///
/// <para>
/// The value is the server's to define — Android stores the whole response and quotes the hash
/// straight back (<c>MediaDataController.loadAvatarConstructor</c>) — but it must change whenever the
/// list changes and it must never be zero for a non-empty list, because zero is what a client sends
/// when it has nothing cached. Since the client re-checks at most once every 24 hours, a hash that
/// fails to move leaves the picker stale for a day.
/// </para>
/// </summary>
public class EmojiListHashHelperTests
{
    private static readonly long[] DocumentIds =
    [
        5357348626559411204,
        5355088357070218139,
        5357465415310124060
    ];

    [Fact]
    public void The_same_ids_hash_the_same()
    {
        EmojiListHashHelper.ComputeHash(DocumentIds)
            .ShouldBe(EmojiListHashHelper.ComputeHash(DocumentIds));
    }

    /// <summary>
    /// An empty list has to hash to zero rather than to some non-zero constant: the store answers
    /// notModified only for a non-zero hash, so zero is what keeps a client with an empty cache from
    /// being told its nothing is up to date.
    /// </summary>
    [Fact]
    public void An_empty_list_hashes_to_zero()
    {
        EmojiListHashHelper.ComputeHash([]).ShouldBe(0);
    }

    [Fact]
    public void A_non_empty_list_never_hashes_to_zero()
    {
        EmojiListHashHelper.ComputeHash(DocumentIds).ShouldNotBe(0);
        // 0 is a legal accumulator input and the only single id that would fold to zero.
        EmojiListHashHelper.ComputeHash([0L]).ShouldNotBe(0);
    }

    [Fact]
    public void Adding_or_removing_an_id_changes_the_hash()
    {
        var baseline = EmojiListHashHelper.ComputeHash(DocumentIds);

        EmojiListHashHelper.ComputeHash(DocumentIds.Take(2)).ShouldNotBe(baseline);
        EmojiListHashHelper.ComputeHash(DocumentIds.Append(5386586994384054997)).ShouldNotBe(baseline);
    }

    /// <summary>
    /// Clients render the grid in the order they receive it, so a reorder is a visible change.
    /// </summary>
    [Fact]
    public void Reordering_the_ids_changes_the_hash()
    {
        EmojiListHashHelper.ComputeHash(DocumentIds.Reverse())
            .ShouldNotBe(EmojiListHashHelper.ComputeHash(DocumentIds));
    }

    /// <summary>
    /// The accumulator is unsigned. Shifting a signed one right — which
    /// <c>MessageSearchMongoHelper.CalcHash</c> does — gives a different answer the moment the
    /// accumulator goes negative, and real custom emoji ids make that happen on the first id.
    /// </summary>
    [Fact]
    public void The_hash_follows_the_documented_unsigned_algorithm()
    {
        var expected = 0UL;
        foreach (var documentId in DocumentIds)
        {
            expected ^= expected >> 21;
            expected ^= expected << 35;
            expected ^= expected >> 4;
            expected += (ulong)documentId;
        }

        EmojiListHashHelper.ComputeHash(DocumentIds).ShouldBe((long)expected);
    }

    /// <summary>
    /// The three lists are different lists on Telegram, and the same ids appear in more than one of
    /// them, so nothing about the hash may depend on which list it describes — only on the contents.
    /// </summary>
    [Fact]
    public void The_hash_depends_only_on_the_ids()
    {
        var fromArray = EmojiListHashHelper.ComputeHash(DocumentIds);
        var fromList = EmojiListHashHelper.ComputeHash(new List<long>(DocumentIds));

        fromList.ShouldBe(fromArray);
    }
}
