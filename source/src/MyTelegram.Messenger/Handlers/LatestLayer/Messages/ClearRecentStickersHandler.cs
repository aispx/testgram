using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Clear recent stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.clearRecentStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ClearRecentStickersHandler(
    IStickerDocumentListStore listStore,
    IStickerUpdateNotifier updateNotifier)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestClearRecentStickers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestClearRecentStickers obj)
    {
        if (await listStore.ClearAsync(StickerDocumentListKind.Recent, input.UserId, obj.Attached))
        {
            await updateNotifier.NotifyRecentAsync(input.UserId, input.AuthKeyId);
        }

        return new TBoolTrue();
    }
}
