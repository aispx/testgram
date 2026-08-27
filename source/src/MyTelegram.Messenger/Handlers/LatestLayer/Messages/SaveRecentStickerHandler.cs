using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Add/remove sticker from recent stickers list
/// Possible errors
/// Code Type Description
/// 400 STICKER_ID_INVALID The provided sticker ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.saveRecentSticker"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveRecentStickerHandler(
    IStickerDocumentListStore listStore,
    IStickerDocumentValidator documentValidator,
    IStickerLimitResolver limitResolver,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSaveRecentSticker, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSaveRecentSticker obj)
    {
        if (obj.Id is not TInputDocument inputDocument)
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
            return null!;
        }

        // attached picks the separate list of mask stickers used on photos; the two never mix.
        var attached = obj.Attached;

        if (obj.Unsave)
        {
            if (await listStore.RemoveAsync(StickerDocumentListKind.Recent, input.UserId, inputDocument.Id,
                    attached))
            {
                await updateNotifier.NotifyRecentAsync(input.UserId, input.AuthKeyId);
            }

            return new TBoolTrue();
        }

        if (!await documentValidator.IsStickerAsync(inputDocument.Id))
        {
            RpcErrors.RpcErrors400.StickerIdInvalid.ThrowRpcError();
        }

        // config.stickers_recent_limit, not a hardcoded 20: the client truncates to the advertised limit
        // before hashing, and a list capped shorter than that means it never stops re-fetching.
        await listStore.AddAsync(StickerDocumentListKind.Recent, input.UserId, inputDocument.Id, attached,
            limitResolver.GetRecentLimit());

        await updateNotifier.NotifyRecentAsync(input.UserId, input.AuthKeyId);

        return new TBoolTrue();
    }
}
