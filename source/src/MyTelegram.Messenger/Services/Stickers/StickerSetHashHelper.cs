namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Computes <c>stickerSet.hash</c>, the value a client quotes back in
/// <c>messages.getStickerSet.hash</c> to be told <c>messages.stickerSetNotModified</c>.
///
/// <para>The hash is defined by the server: clients treat it as an opaque token and only ever compare
/// it for equality, so the only requirements are that it is stable for unchanged content, changes when
/// the content does, and is never zero — zero is the client's "nothing cached" sentinel
/// (<c>MediaDataController.processLoadedStickers</c> re-requests the set immediately when it sees a
/// zero hash alongside a cached copy, so a set answered with <c>hash = 0</c> is fetched again on every
/// single poll).</para>
///
/// <para>Only fields that are identical for every requesting user may go in. In particular the
/// documents' <c>access_hash</c> and <c>file_reference</c> must not: those are minted per session
/// (see <c>AccessHashHelper2</c>), so including them would give each session a different hash for the
/// same set, and a client that switched sessions would never see <c>notModified</c>.</para>
/// </summary>
public static class StickerSetHashHelper
{
    /// <summary>
    /// Hashes the identity and contents of a set: its id and short name, the ordered document ids, and
    /// the alt of each document — the alt is part of it because a re-seed that fixes an alt changes what
    /// clients render and must invalidate their cache.
    /// </summary>
    public static int ComputeHash(long setId, string? shortName, int count,
        IEnumerable<(long DocumentId, string? Alt)> documents)
    {
        unchecked
        {
            // FNV-1a, the same shape EmojiGroupsAppService uses for emoji categories.
            var hash = 2166136261u;

            void Mix(string value)
            {
                foreach (var c in value)
                {
                    hash = (hash ^ c) * 16777619u;
                }

                hash = (hash ^ 0x1Fu) * 16777619u; // field separator
            }

            Mix(setId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Mix(shortName ?? string.Empty);
            Mix(count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            foreach (var (documentId, alt) in documents)
            {
                Mix(documentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Mix(alt ?? string.Empty);
            }

            // stickerSet.hash is an int; fold to a positive value and keep it non-zero so it can never
            // collide with the client's "no cache" sentinel.
            var result = (int)(hash & 0x7FFFFFFF);

            return result == 0 ? 1 : result;
        }
    }
}
