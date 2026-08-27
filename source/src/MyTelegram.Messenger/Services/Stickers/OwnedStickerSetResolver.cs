using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Resolves the set a <c>stickers.*</c> call is about and checks the caller may edit it.
/// </summary>
public interface IOwnedStickerSetResolver
{
    /// <summary>
    /// Throws <c>400 STICKERSET_INVALID</c> when the set does not exist or the caller did not create it —
    /// the same error for both, so the method cannot be used to probe which short names exist.
    /// </summary>
    Task<BsonDocument> ResolveAsync(IRequestInput input, IInputStickerSet? stickerset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The same, for the per-sticker methods, which identify the set only by one of its documents.
    ///
    /// <para>These four — <c>changeSticker</c>, <c>changeStickerPosition</c>,
    /// <c>removeStickerFromSet</c> and <c>replaceSticker</c> — used to look the set up with
    /// <c>StickerSetId == documentId</c>, so all four answered <c>STICKERSET_INVALID</c> for every input
    /// that ever existed.</para>
    /// </summary>
    Task<BsonDocument> ResolveByDocumentAsync(IRequestInput input, IInputDocument? document,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class OwnedStickerSetResolver(IStickerSetStore stickerSetStore)
    : IOwnedStickerSetResolver, ITransientDependency
{
    public async Task<BsonDocument> ResolveAsync(IRequestInput input, IInputStickerSet? stickerset,
        CancellationToken cancellationToken = default)
    {
        var lookup = await stickerSetStore.FindAsync(stickerset, cancellationToken);

        return Authorize(input, lookup.Set);
    }

    public async Task<BsonDocument> ResolveByDocumentAsync(IRequestInput input, IInputDocument? document,
        CancellationToken cancellationToken = default)
    {
        if (document is not TInputDocument inputDocument)
        {
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
            return null!;
        }

        var setDocument = await stickerSetStore.FindByDocumentIdAsync(inputDocument.Id, cancellationToken);
        if (setDocument == null)
        {
            // The document is real but belongs to no set the caller could edit; STICKER_INVALID is what the
            // official server answers for a sticker that is not part of an editable pack.
            RpcErrors.RpcErrors400.StickerInvalid.ThrowRpcError();
        }

        return Authorize(input, setDocument);
    }

    private static BsonDocument Authorize(IRequestInput input, BsonDocument? setDocument)
    {
        if (setDocument == null || setDocument.GetInt64("CreatorUserId") != input.UserId)
        {
            RpcErrors.RpcErrors400.StickersetInvalid.ThrowRpcError();
        }

        return setDocument!;
    }
}
