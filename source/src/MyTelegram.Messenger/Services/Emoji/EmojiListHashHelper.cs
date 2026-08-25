namespace MyTelegram.Messenger.Services.Emoji;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash generation</a>
/// algorithm over the ids of an <c>emojiList</c>.
///
/// <para><c>emojiList.hash</c> is the server's to define: Android stores the whole response and
/// quotes the value straight back (<c>MediaDataController.loadAvatarConstructor</c> sets
/// <c>req.hash = emojiList.hash</c>), so it is only ever compared for equality. Two properties
/// matter. It must change whenever the list changes, or a client sits behind
/// <c>emojiListNotModified</c> with a stale picker — and the check happens at most once every 24
/// hours, so "stale" means stale for a day. And it must not be zero, because a zero hash is what a
/// client sends when it has nothing cached, so answering <c>notModified</c> to it would leave the
/// picker empty forever.</para>
///
/// <para>Order is part of the identity: clients render the grid in the order they receive, so a
/// reordered list is a visible change and has to invalidate the cached copy.</para>
/// </summary>
public static class EmojiListHashHelper
{
    /// <summary>
    /// Hashes the document ids in the given order. The accumulator is unsigned, matching tdlib's
    /// <c>get_vector_hash</c>, Android's <c>MediaDataController.calcHash</c> and tdesktop's
    /// <c>Api::HashUpdate</c> — shifting a signed accumulator right disagrees with every client as
    /// soon as it goes negative, which real document ids make it do immediately.
    /// </summary>
    public static long ComputeHash(IEnumerable<long> documentIds)
    {
        var acc = 0UL;
        var empty = true;

        foreach (var documentId in documentIds)
        {
            empty = false;
            acc ^= acc >> 21;
            acc ^= acc << 35;
            acc ^= acc >> 4;
            acc += (ulong)documentId;
        }

        if (empty)
        {
            return 0;
        }

        var hash = (long)acc;

        // Zero is the client's "nothing cached" sentinel. A non-empty list must never produce it,
        // so nudge the one input that would.
        return hash == 0 ? 1 : hash;
    }
}
