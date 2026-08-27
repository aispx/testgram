using MyTelegram.Messenger.Services.Stickers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Search for <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickersets »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchEmojiStickerSets"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchEmojiStickerSetsHandler(
    IStickerSearchService stickerSearchService,
    IStickerSetMapper stickerSetMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchEmojiStickerSets,
        MyTelegram.Schema.Messages.IFoundStickerSets>
{
    protected override Task<MyTelegram.Schema.Messages.IFoundStickerSets> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestSearchEmojiStickerSets obj)
    {
        return StickerSetSearchResponder.RespondAsync(input, stickerSearchService, stickerSetMapper,
            obj.Q, StickerSetType.CustomEmoji, obj.ExcludeFeatured, obj.Hash);
    }
}
