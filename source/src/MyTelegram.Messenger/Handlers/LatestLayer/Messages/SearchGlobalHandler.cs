namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Search for messages and peers globally
/// Possible errors
/// Code Type Description
/// 400 FOLDER_ID_INVALID Invalid folder ID.
/// 400 INPUT_FILTER_INVALID The specified filter is invalid.
/// 400 SEARCH_QUERY_EMPTY The search query is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchGlobal"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchGlobalHandler(IMessageAppService messageAppService, ITokenizer tokenizer, IQueryProcessor queryProcessor, IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<RequestSearchGlobal, IMessages>
{
    private const int MinTextSearchLength = 2;
    private const int MaxSearchLimit = 100;

    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestSearchGlobal obj)
    {
        var userId = input.UserId;
        var q = NormalizeQuery(obj.Q);
        var messageTypes = MessageFilterHelper.GetMessageTypes(obj.Filter);
        var myMentionsOnly = MessageFilterHelper.IsMyMentionsFilter(obj.Filter);
        var hasFilter = messageTypes.Count > 0 || myMentionsOnly || MessageFilterHelper.IsPinnedFilter(obj.Filter);

        // The media/links/files/music/voice tabs of global search are pre-populated with an empty
        // query plus a filter, so an empty query is only an error when no filter narrows the search.
        // See https://corefork.telegram.org/api/search#global-search
        if (q.Length == 0 && !hasFilter)
        {
            RpcErrors.RpcErrors400.SearchQueryEmpty.ThrowRpcError();
        }

        if (q.Length is > 0 and < MinTextSearchLength)
        {
            if (!hasFilter)
            {
                RpcErrors.RpcErrors400.QueryTooShort.ThrowRpcError();
            }

            // Too short to tokenize, but the filter alone still yields a meaningful result set.
            q = string.Empty;
        }

        var allJoinedChannelIdList = await queryProcessor.ProcessAsync(new GetAllJoinedChannelIdListQuery(input.UserId));
        var tokens = tokenizer.BuildSearchTokens(q);
        var getMessageOutput = await messageAppService.SearchGlobalAsync(new SearchGlobalInput
        {
            OwnerPeerId = userId,
            SelfUserId = userId,
            Limit = NormalizeLimit(obj.Limit),
            Q = q,
            FolderId = obj.FolderId,
            OffsetId = obj.OffsetId,
            JoinedChannelList = allJoinedChannelIdList.ToList(),
            BroadcastsOnly = obj.BroadcastsOnly,
            GroupsOnly = obj.GroupsOnly,
            UsersOnly = obj.UsersOnly,
            Tokens = tokens,
            MessageTypes = messageTypes,
            MyMentionsOnly = myMentionsOnly,
            MinDate = obj.MinDate,
            MaxDate = obj.MaxDate,
            OffsetRate = obj.OffsetRate
        });
        return getHistoryConverterService.ToMessages(input, getMessageOutput, input.Layer);
    }

    private static int NormalizeLimit(int limit)
    {
        return limit <= 0 ? 20 : Math.Min(limit, MaxSearchLimit);
    }

    private static string NormalizeQuery(string? query)
    {
        return query?.Trim() ?? string.Empty;
    }
}
