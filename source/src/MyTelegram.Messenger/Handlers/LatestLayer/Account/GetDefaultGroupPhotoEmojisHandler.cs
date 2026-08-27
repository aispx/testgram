using MyTelegram.Messenger.Services.Emoji;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get a set of suggested <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers</a> that can be <a href="https://corefork.telegram.org/api/files#sticker-profile-pictures">used as group picture</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getDefaultGroupPhotoEmojis"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDefaultGroupPhotoEmojisHandler(IDefaultEmojiListAppService defaultEmojiListAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetDefaultGroupPhotoEmojis, MyTelegram.Schema.IEmojiList>
{
    protected override Task<MyTelegram.Schema.IEmojiList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetDefaultGroupPhotoEmojis obj)
    {
        return defaultEmojiListAppService.GetAsync(DefaultEmojiListKind.GroupPhoto, obj.Hash);
    }
}
