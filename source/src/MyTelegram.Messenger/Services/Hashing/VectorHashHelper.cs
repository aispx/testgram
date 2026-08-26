namespace MyTelegram.Messenger.Services.Hashing;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash generation</a>
/// algorithm every client uses to ask "is my cached copy still current?".
///
/// <para>The value is opaque — clients only compare it for equality — but the algorithm is not free
/// to choose: the client computes the hash of <b>its own</b> cached list and sends that, so the
/// server has to arrive at the same number from the same data or <c>*NotModified</c> can never
/// fire and the list is re-downloaded on every poll.</para>
///
/// <para>Reference implementations, all identical: tdlib <c>td/telegram/misc.cpp get_vector_hash</c>,
/// Telegram Android <c>MediaDataController.calcHash</c>, tdesktop <c>Api::HashUpdate</c>.</para>
///
/// <para>The accumulator is <b>unsigned</b>. <c>MessageSearchMongoHelper.CalcHash</c> shifts a signed
/// <c>long</c> right and therefore disagrees with every client as soon as the accumulator goes
/// negative, which real ids make it do immediately — do not reuse that one for anything a client
/// quotes back.</para>
/// </summary>
public static class VectorHashHelper
{
    /// <summary>
    /// Hashes the numbers in the given order. Order is part of the identity: clients render lists in
    /// the order they receive them, so a reordered list is a visible change and must invalidate the
    /// cached copy.
    /// </summary>
    public static long ComputeHash(IEnumerable<long> numbers)
    {
        var acc = 0UL;

        foreach (var number in numbers)
        {
            acc = Mix(acc, number);
        }

        return (long)acc;
    }

    /// <summary>
    /// Folds one number into an accumulator — the exact body of Android's
    /// <c>calcHash(long hash, long id)</c>. Exposed for callers that interleave values, such as the
    /// featured-stickerset hash, which appends an extra <c>1</c> after every unread set id.
    /// </summary>
    public static ulong Mix(ulong acc, long number)
    {
        acc ^= acc >> 21;
        acc ^= acc << 35;
        acc ^= acc >> 4;

        return acc + (ulong)number;
    }

    /// <summary>
    /// Same as <see cref="ComputeHash(IEnumerable{long})"/> but never returns zero for a non-empty
    /// input. Use it wherever the server <i>defines</i> the hash instead of reproducing a client's
    /// computation (<c>emojiList.hash</c>, <c>stickerSet.hash</c>): zero is what a client sends when
    /// it has nothing cached, so a non-empty list that hashed to zero could be answered
    /// <c>notModified</c> and leave the client empty forever. An empty list still hashes to 0 on
    /// purpose.
    /// </summary>
    public static long ComputeNonZeroHash(IEnumerable<long> numbers)
    {
        var acc = 0UL;
        var empty = true;

        foreach (var number in numbers)
        {
            empty = false;
            acc = Mix(acc, number);
        }

        if (empty)
        {
            return 0;
        }

        var hash = (long)acc;

        return hash == 0 ? 1 : hash;
    }
}
