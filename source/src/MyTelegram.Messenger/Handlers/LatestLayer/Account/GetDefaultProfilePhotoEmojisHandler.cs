using MyTelegram.Messenger.Services.Emoji;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get a set of suggested <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickers</a> that can be <a href="https://corefork.telegram.org/api/files#sticker-profile-pictures">used as profile picture</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getDefaultProfilePhotoEmojis"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDefaultProfilePhotoEmojisHandler(IDefaultEmojiListAppService defaultEmojiListAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetDefaultProfilePhotoEmojis, MyTelegram.Schema.IEmojiList>
{
    protected override Task<MyTelegram.Schema.IEmojiList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetDefaultProfilePhotoEmojis obj)
    {
        return defaultEmojiListAppService.GetAsync(DefaultEmojiListKind.ProfilePhoto, obj.Hash);
    }
}
