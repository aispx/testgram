namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash generation</a>
/// algorithm, over the ids of the documents in a saved-GIF list.
///
/// <para>This exists separately from <c>MessageSearchMongoHelper.CalcHash</c> because that one
/// shifts a signed <c>long</c> right, while the specification and every client shift
/// <b>unsigned</b>. The accumulator regularly goes negative for real document ids, so the two
/// disagree — and a saved-GIF hash that disagrees with the client means
/// <c>messages.savedGifsNotModified</c> can never fire and the list is re-downloaded on every
/// poll.</para>
///
/// <para>Reference implementations, all identical:
/// tdlib <c>td/telegram/misc.cpp get_vector_hash</c>, Telegram Android
/// <c>MediaDataController.calcHash</c>, tdesktop <c>Api::HashUpdate</c>.</para>
/// </summary>
public static class SavedGifHashHelper
{
    /// <summary>
    /// Hashes the document ids in the given order. Order is significant — the same set of ids in a
    /// different order produces a different hash, which is why the saved-GIF list must always be
    /// returned newest-first.
    /// </summary>
    public static long ComputeHash(IEnumerable<long> documentIds)
    {
        var acc = 0UL;

        foreach (var documentId in documentIds)
        {
            acc ^= acc >> 21;
            acc ^= acc << 35;
            acc ^= acc >> 4;
            acc += (ulong)documentId;
        }

        return (long)acc;
    }

    /// <summary>
    /// How many leading ids Telegram Android feeds into its own hash
    /// (<c>MediaDataController.calcDocumentsHash</c> defaults to <c>maxCount = 200</c>), regardless
    /// of the Premium limit of 400. A Premium account with more than 200 saved GIFs therefore sends
    /// a hash over a truncated list, and callers accept that variant as well so those clients still
    /// get caching instead of a full list on every poll.
    /// </summary>
    public const int AndroidHashLimit = 200;
}
