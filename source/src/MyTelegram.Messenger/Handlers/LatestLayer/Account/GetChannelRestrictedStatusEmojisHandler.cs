namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Returns fetch the full list of <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji IDs »</a> that cannot be used in <a href="https://corefork.telegram.org/api/emoji-status">channel emoji statuses »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getChannelRestrictedStatusEmojis"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetChannelRestrictedStatusEmojisHandler(
    IChannelEmojiStatusValidator channelEmojiStatusValidator)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetChannelRestrictedStatusEmojis, MyTelegram.Schema.IEmojiList>
{
    protected override async Task<MyTelegram.Schema.IEmojiList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetChannelRestrictedStatusEmojis obj)
    {
        // Read from the channel_restricted_status_emojis collection: empty on a server that
        // restricts nothing, which is the correct answer rather than a stub.
        var documentIds = await channelEmojiStatusValidator.GetRestrictedDocumentIdsAsync();
        var hash = EmojiStatusesHelper.CalculateHash(documentIds);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TEmojiListNotModified();
        }

        return new TEmojiList { DocumentId = new TVector<long>(documentIds) };
    }
}
