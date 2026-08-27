using MyTelegram.Messenger.Services.Gifs;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: the <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash</a> that lets
/// <c>messages.getSavedGifs</c> answer <c>messages.savedGifsNotModified</c>.
///
/// <para>
/// The value has to match what the client computed byte for byte, or caching never engages and the whole
/// list is re-sent on every poll. All three reference implementations — tdlib
/// <c>get_vector_hash</c>, Telegram Android <c>MediaDataController.calcHash</c> and tdesktop
/// <c>Api::HashUpdate</c> — shift the accumulator right <b>unsigned</b>, which is the one detail a C#
/// <c>long</c> gets wrong for free.
/// </para>
/// </summary>
public class SavedGifHashHelperTests
{
    /// <summary>
    /// The client algorithm, transcribed from Telegram Android's <c>MediaDataController.calcHash</c>
    /// (<c>hash ^= hash &gt;&gt;&gt; 21</c> — Java's unsigned shift), used here as an independent oracle.
    /// </summary>
    private static long ReferenceHash(IEnumerable<long> ids)
    {
        var hash = 0UL;
        foreach (var id in ids)
        {
            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += (ulong)id;
        }

        return (long)hash;
    }

    /// <summary>
    /// The same algorithm written with C#'s <i>signed</i> right shift — what a <c>long</c> accumulator
    /// gives you by accident. Used to prove the two are not interchangeable.
    /// </summary>
    private static long SignedShiftHash(IEnumerable<long> ids)
    {
        var hash = 0L;
        foreach (var id in ids)
        {
            hash ^= hash >> 21;
            hash ^= hash << 35;
            hash ^= hash >> 4;
            hash += id;
        }

        return hash;
    }

    [Fact]
    public void An_empty_list_hashes_to_zero()
    {
        SavedGifHashHelper.ComputeHash([]).ShouldBe(0);
    }

    [Fact]
    public void The_hash_matches_the_client_algorithm()
    {
        // Real document ids taken from this server's own GIFs.
        long[] ids = [2060835009452392821, 122669354334652493, 2547261145952940266];

        var hash = SavedGifHashHelper.ComputeHash(ids);

        hash.ShouldBe(ReferenceHash(ids));
        // Three ordinary ids are already enough for a signed shift to diverge, which is why
        // MessageSearchMongoHelper.CalcHash cannot be reused for this.
        hash.ShouldNotBe(SignedShiftHash(ids));
    }

    [Fact]
    public void The_hash_matches_the_client_algorithm_once_the_accumulator_goes_negative()
    {
        long[] ids = [long.MaxValue, 1, 1, 1];

        var hash = SavedGifHashHelper.ComputeHash(ids);

        hash.ShouldBe(ReferenceHash(ids));
        hash.ShouldBeLessThan(0);
        hash.ShouldNotBe(SignedShiftHash(ids));
    }

    [Fact]
    public void Order_changes_the_hash()
    {
        long[] ids = [111, 222, 333];
        long[] reordered = [222, 111, 333];

        SavedGifHashHelper.ComputeHash(ids).ShouldNotBe(SavedGifHashHelper.ComputeHash(reordered));
    }

    [Fact]
    public void A_prefix_hashes_differently_from_the_whole_list()
    {
        // Why the handler also accepts the hash of the first 200 ids: Android hashes a prefix for a
        // Premium list longer than that, and the two values are not interchangeable.
        long[] ids = [11, 22, 33, 44];

        SavedGifHashHelper.ComputeHash(ids.Take(2)).ShouldNotBe(SavedGifHashHelper.ComputeHash(ids));
    }

    [Fact]
    public void The_android_prefix_limit_is_two_hundred()
    {
        SavedGifHashHelper.AndroidHashLimit.ShouldBe(200);
    }
}
