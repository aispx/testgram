using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Gets featured custom emoji stickersets.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFeaturedEmojiStickers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetFeaturedEmojiStickersHandler(IFeaturedStickerSetListService listService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFeaturedEmojiStickers,
        MyTelegram.Schema.Messages.IFeaturedStickers>
{
    protected override Task<MyTelegram.Schema.Messages.IFeaturedStickers> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetFeaturedEmojiStickers obj)
    {
        return listService.GetFeaturedAsync(input, StickerSetType.CustomEmoji, obj.Hash);
    }
}
