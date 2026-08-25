using MyTelegram.Messenger.Services.Gifs;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get saved GIFs.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getSavedGifs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedGifsHandler(
    ISavedGifStore savedGifStore,
    ISavedGifLimitResolver limitResolver,
    IGifDocumentReader documentReader)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedGifs, MyTelegram.Schema.Messages.ISavedGifs>
{
    protected override async Task<ISavedGifs> HandleCoreAsync(IRequestInput input, RequestGetSavedGifs obj)
    {
        var limit = await limitResolver.GetLimitAsync(input.UserId);
        var savedIds = await savedGifStore.GetOrderedIdsAsync(input.UserId, limit);

        var documents = await documentReader.GetAsync(savedIds);

        // A document we return but the client discards leaves its list shorter than ours, and since
        // the hash is computed over the list in order, that mismatch is permanent: the client would
        // re-download the whole list on every poll forever. So the list we serve and the list we
        // store are reconciled here — anything that is no longer a GIF is dropped from both.
        var gifs = new TVector<MyTelegram.Schema.IDocument>();
        var orderedIds = new List<long>(savedIds.Count);
        var staleIds = new List<long>();

        foreach (var documentId in savedIds)
        {
            if (!documents.TryGetValue(documentId, out var document) || !GifDocumentHelper.IsAnimatedMp4(document))
            {
                staleIds.Add(documentId);
                continue;
            }

            gifs.Add(documentReader.Map(document));
            orderedIds.Add(documentId);
        }

        if (staleIds.Count > 0)
        {
            await savedGifStore.RemoveManyAsync(input.UserId, staleIds);
        }

        var hash = SavedGifHashHelper.ComputeHash(orderedIds);

        if (obj.Hash != 0 && IsClientUpToDate(obj.Hash, hash, orderedIds))
        {
            return new TSavedGifsNotModified();
        }

        return new TSavedGifs
        {
            Hash = hash,
            Gifs = gifs
        };
    }

    /// <summary>
    /// Whether the hash the client sent describes the list we are about to return.
    ///
    /// <para>Telegram Android feeds only the first 200 documents into its hash
    /// (<c>MediaDataController.calcDocumentsHash</c> defaults to <c>maxCount = 200</c>) even when the
    /// Premium limit is 400, so for a longer list its hash is over a prefix. Accepting that variant
    /// too is what keeps caching working for those clients instead of resending 400 documents every
    /// hour.</para>
    /// </summary>
    private static bool IsClientUpToDate(long clientHash, long fullHash, List<long> orderedIds)
    {
        if (clientHash == fullHash)
        {
            return true;
        }

        return orderedIds.Count > SavedGifHashHelper.AndroidHashLimit
            && clientHash == SavedGifHashHelper.ComputeHash(orderedIds.Take(SavedGifHashHelper.AndroidHashLimit));
    }
}
