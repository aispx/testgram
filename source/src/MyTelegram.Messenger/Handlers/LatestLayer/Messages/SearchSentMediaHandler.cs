namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// View and search recently sent media.<br/>
/// This method does not support pagination.
/// Possible errors
/// Code Type Description
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchSentMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchSentMediaHandler(
    IMessageAppService messageAppService,
    ITokenizer tokenizer,
    IGetHistoryConverterService getHistoryConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchSentMedia, MyTelegram.Schema.Messages.IMessages>
{
    private const int MinTextSearchLength = 2;
    private const int MaxSearchLimit = 100;

    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSearchSentMedia obj)
    {
        // The method lists media we sent ourselves, so it only makes sense for a media filter.
        var messageTypes = MessageFilterHelper.GetMessageTypes(obj.Filter);
        if (messageTypes.Count == 0)
        {
            RpcErrors.RpcErrors400.FilterNotSupported.ThrowRpcError();
        }

        var userId = input.UserId;
        var q = obj.Q?.Trim() ?? string.Empty;
        if (q.Length < MinTextSearchLength)
        {
            q = string.Empty;
        }

        var tokens = tokenizer.BuildSearchTokens(q);
        var getMessageOutput = await messageAppService.SearchAsync(new SearchInput
        {
            OwnerPeerId = userId,
            SelfUserId = userId,
            Limit = obj.Limit <= 0 ? 20 : Math.Min(obj.Limit, MaxSearchLimit),
            Q = q,
            // Across every chat, restricted to messages we sent ourselves.
            Peer = new Peer(PeerType.Empty, 0),
            FilterSenderUserId = userId,
            MessageTypes = messageTypes,
            Tokens = tokens
        });

        var converted = getHistoryConverterService.ToMessages(input, getMessageOutput, input.Layer);
        var (messages, chats, users) = GetSavedDialogsHandler.ExtractMessages(converted);

        // No pagination support: answering with a messagesSlice would make clients page through a
        // cursor this method does not honour.
        return new TMessages
        {
            Messages = [.. messages],
            Chats = [.. chats],
            Users = [.. users],
            Topics = new TVector<IForumTopic>()
        };
    }
}
