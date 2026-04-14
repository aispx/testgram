namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Returns identifiers of pinned or always included chats from a chat folder imported using a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>, which are suggested to be left when the chat folder is deleted.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.getLeaveChatlistSuggestions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetLeaveChatlistSuggestionsHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestGetLeaveChatlistSuggestions, TVector<MyTelegram.Schema.IPeer>>
{
    private readonly IQueryProcessor _queryProcessor;

    public GetLeaveChatlistSuggestionsHandler(IQueryProcessor queryProcessor)
    {
        _queryProcessor = queryProcessor;
    }

    protected override async Task<TVector<MyTelegram.Schema.IPeer>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestGetLeaveChatlistSuggestions obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        var filterId = chatlistFilter.FilterId;

        // 2. Get user's filters
        var userFilters = await _queryProcessor.ProcessAsync(
            new GetDialogFiltersQuery(input.UserId),
            CancellationToken.None);

        var filter = userFilters.FirstOrDefault(f => f.FolderId == filterId);
        if (filter == null)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
        }

        // 3. Return all included peers as suggestions to leave
        var suggestions = new TVector<IPeer>();

        foreach (var peer in filter.Filter.IncludePeers)
        {
            suggestions.Add(new Peer(peer.PeerType, peer.PeerId).ToPeer());
        }

        return suggestions;
    }
}