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
///
/// <para>Everything fed in comes from the catalogue row alone, and that is a hard requirement rather
/// than an optimisation: the same number has to come out of <c>messages.getStickerSet</c> and out of
/// the list methods (<c>getAllStickers</c> and friends), because a client caches whichever copy it saw
/// last and then hashes the list from the cached <c>set.hash</c> values
/// (Android <c>MediaDataController.calcStickersHash</c>). The list methods cannot afford to load every
/// document of every installed set on each poll, so the per-document <c>alt</c> stored on the document
/// row is deliberately <i>not</i> part of this hash — the emoji comes from the set's own
/// <c>stickerPack</c> entries instead, and <c>Version</c> covers any other edit to the row, including a
/// re-seed that only rewrites document alts.</para>
/// </summary>
public static class StickerSetHashHelper
{
    /// <summary>
    /// Hashes the identity and contents of a set: its id, short name, sticker count and revision, then
    /// the ordered document ids each paired with the emoji of the pack it belongs to.
    /// </summary>
    public static int ComputeHash(long setId, string? shortName, int count, long version,
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
            Mix(version.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    /// <summary>
    /// The single entry point every caller should use: derives the hash straight from the catalogue row,
    /// so no two methods can disagree about a set's hash.
    /// </summary>
    /// <param name="diceEmoticon">
    /// For <c>inputStickerSetDice</c>, whose catalogue row has no packs of its own — the emoji stands in
    /// as the alt of every document, matching what the response carries.
    /// </param>
    public static int ComputeHash(MongoDB.Bson.BsonDocument stickerSetDocument, string? diceEmoticon = null)
    {
        var documentIds = stickerSetDocument.GetInt64List("DocumentIds");
        var altByDocumentId = StickerSetPackReader.BuildAltByDocumentId(stickerSetDocument, diceEmoticon);

        return ComputeHash(
            stickerSetDocument.GetInt64("StickerSetId"),
            StickerSetPackReader.ReadShortName(stickerSetDocument),
            stickerSetDocument.GetInt32("Count", documentIds.Count),
            stickerSetDocument.GetInt64("Version"),
            documentIds.Select(p => (p, altByDocumentId.GetValueOrDefault(p))));
    }
}
