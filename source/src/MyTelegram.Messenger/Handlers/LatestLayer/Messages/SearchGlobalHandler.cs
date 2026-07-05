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
        if (q.Length == 0)
        {
            RpcErrors.RpcErrors400.SearchQueryEmpty.ThrowRpcError();
        }

        if (q.Length is > 0 and < MinTextSearchLength)
        {
            RpcErrors.RpcErrors400.QueryTooShort.ThrowRpcError();
        }

        var allJoinedChannelIdList = await queryProcessor.ProcessAsync(new GetAllJoinedChannelIdListQuery(input.UserId));
        var tokens = tokenizer.BuildSearchTokens(q);
        var getMessageOutput = await messageAppService.SearchGlobalAsync(new SearchGlobalInput { OwnerPeerId = userId, SelfUserId = userId, Limit = NormalizeLimit(obj.Limit), Q = q, FolderId = obj.FolderId, OffsetId = obj.OffsetId, JoinedChannelList = allJoinedChannelIdList.ToList(), BroadcastsOnly = obj.BroadcastsOnly, GroupsOnly = obj.GroupsOnly, UsersOnly = obj.UsersOnly, Tokens = tokens });
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
