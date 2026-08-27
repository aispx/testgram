using MyTelegram.Messenger.Services.Gifs;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Add GIF to saved gifs list
/// Possible errors
/// Code Type Description
/// 400 GIF_ID_INVALID The provided GIF ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.saveGif"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveGifHandler(
    ISavedGifStore savedGifStore,
    ISavedGifLimitResolver limitResolver,
    ISavedGifUpdateNotifier updateNotifier,
    IGifDocumentReader documentReader,
    IGifMp4ConversionStore conversionStore,
    IAccessHashHelper2 accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSaveGif, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSaveGif obj)
    {
        if (obj.Id is not TInputDocument inputDocument)
        {
            RpcErrors.RpcErrors400.GifIdInvalid.ThrowRpcError();
            return null!;
        }

        // Access-hash validation is not applied to this request upstream, so it happens here.
        await accessHashHelper.CheckAccessHashAsync(input, inputDocument.Id, inputDocument.AccessHash,
            AccessHashType.Document);

        if (obj.Unsave)
        {
            // Unsaving something that is not in the list is not an error - tdlib and tdesktop both
            // treat a false answer as "resync everything", so the only failure signal is an RPC error.
            var removed = await savedGifStore.RemoveAsync(input.UserId, inputDocument.Id);

            // The original id may have been converted on upload; drop the MPEG4 twin as well, since
            // that is what the list actually holds.
            var convertedId = await conversionStore.GetMp4DocumentIdAsync(inputDocument.Id);
            if (convertedId.HasValue)
            {
                removed |= await savedGifStore.RemoveAsync(input.UserId, convertedId.Value);
            }

            if (removed)
            {
                await updateNotifier.NotifyAsync(input.UserId, input.AuthKeyId);
            }

            return new TBoolTrue();
        }

        var documentId = await ResolveGifDocumentIdAsync(inputDocument.Id);

        var limit = await limitResolver.GetLimitAsync(input.UserId);
        await savedGifStore.AddAsync(input.UserId, documentId, limit);
        await updateNotifier.NotifyAsync(input.UserId, input.AuthKeyId);

        return new TBoolTrue();
    }

    /// <summary>
    /// The document id to store: the one that was passed in when it is already an MPEG4 animation,
    /// otherwise the MPEG4 the server produced from it on upload.
    ///
    /// <para>Only MPEG4 may go into the list. tdlib refuses to save anything else locally ("Only
    /// MPEG4 animations can be saved") and tdesktop drops non-<c>isGifv()</c> documents out of the
    /// list it receives, which would silently desynchronise its list from ours forever.</para>
    /// </summary>
    private async Task<long> ResolveGifDocumentIdAsync(long documentId)
    {
        var document = await documentReader.GetAsync(documentId);
        if (document == null)
        {
            RpcErrors.RpcErrors400.GifIdInvalid.ThrowRpcError();
        }

        if (GifDocumentHelper.IsAnimatedMp4(document))
        {
            return documentId;
        }

        var convertedId = await conversionStore.GetMp4DocumentIdAsync(documentId);
        if (convertedId.HasValue && GifDocumentHelper.IsAnimatedMp4(await documentReader.GetAsync(convertedId.Value)))
        {
            return convertedId.Value;
        }

        RpcErrors.RpcErrors400.GifIdInvalid.ThrowRpcError();
        return 0;
    }
}
