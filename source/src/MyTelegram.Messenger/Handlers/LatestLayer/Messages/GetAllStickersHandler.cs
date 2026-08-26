using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Get all installed stickers
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAllStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAllStickersHandler(IInstalledStickerSetListService listService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAllStickers,
        MyTelegram.Schema.Messages.IAllStickers>
{
    protected override Task<MyTelegram.Schema.Messages.IAllStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetAllStickers obj)
    {
        return listService.GetAsync(input, StickerSetType.Regular, obj.Hash);
    }
}
