namespace MyTelegram.Messenger.Services.Gifs;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/offsets#hash-generation">hash generation</a>
/// algorithm, over the ids of the documents in a saved-GIF list.
///
/// <para>The algorithm itself lives in <see cref="VectorHashHelper"/>; this type only records what
/// gets fed into it for saved GIFs, and the Android truncation quirk below.</para>
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
        return VectorHashHelper.ComputeHash(documentIds);
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
