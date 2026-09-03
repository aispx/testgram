using MyTelegram.Messenger.Services.Ringtones;

namespace MyTelegram.Messenger.Tests.Ringtones;

/// <summary>
/// Feature: the hash that lets <c>account.getSavedRingtones</c> answer
/// <a href="https://corefork.telegram.org/constructor/account.savedRingtonesNotModified">account.savedRingtonesNotModified</a>.
///
/// <para>
/// This one is defined by the <b>server</b>: every client stores the value from the response and quotes it
/// back unchanged (Android in preferences, tdesktop in <c>_list.hash</c>, iOS in the cached sound list,
/// tdlib in its log event), and none of them computes one. So the only two things that matter are that it
/// is stable across restarts — a value derived from process state re-downloads the list on every deploy —
/// and that a non-empty list never hashes to 0, because 0 is what a client sends when it has nothing
/// cached and could therefore never match.
/// </para>
/// </summary>
public class SavedRingtoneHashHelperTests
{
    /// <summary>
    /// The documented algorithm, transcribed with Java's unsigned shift, as an independent oracle.
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

    [Fact]
    public void The_hash_follows_the_documented_algorithm()
    {
        long[] ids = [5_204_474_871_112_567_500, 12_345, 999_999_999_999];

        SavedRingtoneHashHelper.ComputeHash(ids).ShouldBe(ReferenceHash(ids));
    }

    [Fact]
    public void The_same_list_hashes_the_same_way_every_time()
    {
        long[] ids = [111, 222, 333];

        SavedRingtoneHashHelper.ComputeHash(ids).ShouldBe(SavedRingtoneHashHelper.ComputeHash(ids));
    }

    [Fact]
    public void Reordering_the_list_changes_the_hash()
    {
        SavedRingtoneHashHelper.ComputeHash([111L, 222L, 333L])
            .ShouldNotBe(SavedRingtoneHashHelper.ComputeHash([333L, 222L, 111L]));
    }

    [Fact]
    public void Adding_a_sound_changes_the_hash()
    {
        SavedRingtoneHashHelper.ComputeHash([111L, 222L])
            .ShouldNotBe(SavedRingtoneHashHelper.ComputeHash([333L, 111L, 222L]));
    }

    /// <summary>
    /// Zero is the client's "nothing cached" value, so a list that hashes to it would be re-fetched forever.
    /// </summary>
    [Fact]
    public void A_non_empty_list_never_hashes_to_zero()
    {
        // 0 alone folds to 0 with the raw algorithm; ComputeNonZeroHash is what keeps it out of the response.
        SavedRingtoneHashHelper.ComputeHash([0L]).ShouldNotBe(0);
        SavedRingtoneHashHelper.ComputeHash([111L]).ShouldNotBe(0);
    }

    [Fact]
    public void An_empty_list_hashes_to_zero()
    {
        SavedRingtoneHashHelper.ComputeHash([]).ShouldBe(0);
    }
}
