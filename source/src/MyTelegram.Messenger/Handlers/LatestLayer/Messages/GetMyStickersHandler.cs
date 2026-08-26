using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Fetch <a href="https://corefork.telegram.org/api/stickers">the sticker sets</a> owned by the current user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getMyStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetMyStickersHandler(
    IStickerSetStore stickerSetStore,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetMyStickers,
        MyTelegram.Schema.Messages.IMyStickers>
{
    protected override async Task<MyTelegram.Schema.Messages.IMyStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetMyStickers obj)
    {
        var setDocuments = await stickerSetStore.FindByCreatorAsync(input.UserId, obj.OffsetId, obj.Limit);

        return new MyTelegram.Schema.Messages.TMyStickers
        {
            // The total, not the page size: the client uses it to decide whether to keep paginating.
            Count = await stickerSetStore.CountByCreatorAsync(input.UserId),
            Sets = new TVector<IStickerSetCovered>(
                await stickerSetMapper.BuildCoveredAsync(input, setDocuments, false))
        };
    }
}
