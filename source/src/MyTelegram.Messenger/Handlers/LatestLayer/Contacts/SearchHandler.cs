using RequestSearch = MyTelegram.Schema.Contacts.RequestSearch;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Returns users found by username substring.
/// Possible errors
/// Code Type Description
/// 400 QUERY_TOO_SHORT The query string is too short.
/// 400 SEARCH_QUERY_EMPTY The search query is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.search"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchHandler(IContactAppService contactAppService, ISearchConverterService searchConverterService) : RpcResultObjectHandler<RequestSearch, Schema.Contacts.IFound>
{
    private const int MinQueryLength = 2;
    private const int MaxSearchLimit = 50;

    protected override async Task<IFound> HandleCoreAsync(IRequestInput input, RequestSearch obj)
    {
        var userId = input.UserId;
        var q = NormalizeQuery(obj.Q);
        if (q.Length == 0)
        {
            RpcErrors.RpcErrors400.SearchQueryEmpty.ThrowRpcError();
        }

        if (q.Length < MinQueryLength)
        {
            RpcErrors.RpcErrors400.QueryTooShort.ThrowRpcError();
        }

        var limit = obj.Limit <= 0 ? 20 : Math.Min(obj.Limit, MaxSearchLimit);
        var searchResult = await contactAppService.SearchAsync(userId, q, limit);
        return searchConverterService.ToFound(input, searchResult, input.Layer);
    }

    private static string NormalizeQuery(string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        return q.StartsWith("@") ? q[1..].Trim() : q;
    }
}
