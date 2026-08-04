namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get a list of default suggested <a href="https://corefork.telegram.org/api/emoji-status">channel emoji statuses</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getChannelDefaultEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelDefaultEmojiStatusesHandler(
    IChannelEmojiStatusValidator channelEmojiStatusValidator)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetChannelDefaultEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetChannelDefaultEmojiStatuses obj)
    {
        // Only emoji from sets flagged channel_emoji_status may be used as a channel status, and the
        // restricted ones are filtered out, so this is exactly the set channels can pick from.
        var documentIds = await channelEmojiStatusValidator.GetAllowedDocumentIdsAsync();

        return EmojiStatusesHelper.ToEmojiStatuses(documentIds, obj.Hash);
    }
}
