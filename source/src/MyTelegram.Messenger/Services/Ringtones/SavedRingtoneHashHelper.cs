namespace MyTelegram.Messenger.Services.Ringtones;

/// <summary>
/// The hash of a saved notification sound list.
///
/// <para>Unlike the saved-GIF or trending-sticker hashes, this one is <b>defined by the server</b>:
/// every client stores the value from the response verbatim and quotes it back on the next call —
/// Android in its preferences (<c>RingtoneDataStore</c>: <c>queryHash = res.hash</c>, sent as
/// <c>req.hash</c>), tdesktop in <c>_list.hash = data.vhash().v</c>, iOS in the cached
/// <c>NotificationSoundList.hash</c>, tdlib in its <c>RingtoneListLogEvent</c>. None of them computes
/// one, so nothing constrains the algorithm — only these two properties do:</para>
/// <list type="bullet">
/// <item><description>It has to survive a restart. A value derived from process state (<c>HashCode</c>,
/// a random seed) changes on every deploy and the whole list is re-downloaded on every poll.</description></item>
/// <item><description>It has to be non-zero for a non-empty list. Zero is what a client sends when it
/// has nothing cached, so a list answered with <c>hash = 0</c> can never match.</description></item>
/// </list>
/// <para>An empty list hashes to 0 on purpose: a client with an empty cache must never be told that its
/// nothing is current.</para>
/// See https://corefork.telegram.org/api/ringtones#getting-notification-sounds
/// </summary>
public static class SavedRingtoneHashHelper
{
    /// <summary>
    /// Hashes the document ids in the order they are served. Order is part of the identity — clients
    /// render the list exactly as received (iOS and tdesktop put a new sound first), so a reordered list
    /// is a visible change and must invalidate the cached copy.
    /// </summary>
    public static long ComputeHash(IEnumerable<long> documentIds)
    {
        return VectorHashHelper.ComputeNonZeroHash(documentIds);
    }
}
