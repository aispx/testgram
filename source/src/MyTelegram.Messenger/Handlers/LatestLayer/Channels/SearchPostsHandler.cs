using MongoDB.Driver;
using MyTelegram.Messenger.Handlers.LatestLayer.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Globally search for posts from public <a href="https://corefork.telegram.org/api/channel">channels »</a> (<em>including</em> those we aren't a member of) containing either a specific hashtag, <em>or</em> a full text query.Exactly one of <code>query</code> and <code>hashtag</code> must be set.
/// Possible errors
/// Code Type Description
/// 420 FROZEN_METHOD_INVALID The current account is <a href="https://corefork.telegram.org/api/auth#frozen-accounts">frozen</a>, and thus cannot execute the specified action.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.searchPosts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchPostsHandler(IQueryProcessor queryProcessor, ITokenizer tokenizer, IChatConverterService chatConverterService, IUserConverterService userConverterService, IMessageConverterService messageConverterService, IPeerHelper peerHelper, IMessageAppService messageAppService, IMongoDatabase mongoDatabase, IMinConstructorReducer minConstructorReducer) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestSearchPosts, MyTelegram.Schema.Messages.IMessages>
{
    private const int MinTextSearchLength = 2;
    private const int MaxSearchLimit = 100;

    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestSearchPosts obj)
    {
        var peer = peerHelper.GetPeer(obj.OffsetPeer);
        var query = NormalizeQuery(obj.Query);
        var hashtag = NormalizeQuery(obj.Hashtag).TrimStart('#');

        // Exactly one of query/hashtag must be set.
        // See https://corefork.telegram.org/method/channels.searchPosts
        if ((query.Length > 0) == (hashtag.Length > 0))
        {
            if (query.Length == 0)
            {
                RpcErrors.RpcErrors400.SearchQueryEmpty.ThrowRpcError();
            }

            RpcErrors.RpcErrors400.HashtagInvalid.ThrowRpcError();
        }

        if (query.Length is > 0 and < MinTextSearchLength)
        {
            RpcErrors.RpcErrors400.QueryTooShort.ThrowRpcError();
        }

        // The first page consumes quota; paging through an already-paid search must stay free.
        if (obj.OffsetId == 0 && obj.OffsetRate == 0)
        {
            await ChargeSearchAsync(input, obj);
        }

        var limit = NormalizeLimit(obj.Limit);
        var tokens = tokenizer.BuildSearchTokens(query);
        var messageReadModels = await queryProcessor.ProcessAsync(new SearchPostsQuery(hashtag, query, tokens, obj.OffsetRate, peer.PeerId, obj.OffsetId, limit));
        var messages = messageConverterService.ToMessageList(input.UserId, messageReadModels, [], [], [], input.Layer);
        var(userIds, channelIds) = messageAppService.GetExtraPeerIds(messageReadModels);
        var channelIdList = channelIds.ToList();
        var channelMemberReadModels = await queryProcessor.ProcessAsync(new GetChannelMemberListByChannelIdListQuery(input.UserId, channelIdList));
        var channels = await chatConverterService.GetChannelListAsync(input, channelIdList, channelMemberReadModels, input.Layer);
        var users = await userConverterService.GetUserListAsync(input, userIds.ToList(), false, false, input.Layer);

        // A global post search is the clearest case of seeing peers the caller has nothing to do
        // with: they are reading public channels they are not a member of, so senders and referenced
        // channels go out as min. See https://corefork.telegram.org/api/min
        minConstructorReducer.Reduce(input, messageReadModels, users, channels);

        if (messageReadModels.Count == limit && messageReadModels.Count > 0)
        {
            var nextRate = messageReadModels.Max(p => p.Date);
            var totalCount = await queryProcessor.ProcessAsync(new GetPostsCountQuery(hashtag, query, tokens, obj.OffsetRate, peer.PeerId, obj.OffsetId));
            return new TMessagesSlice
            {
                Count = totalCount,
                Chats = [..channels],
                Messages = [..messages],
                Users = [..users],
                NextRate = nextRate,
                Topics = new TVector<IForumTopic>()
            };
        }

        return new TMessages
        {
            Chats = [..channels],
            Messages = [..messages],
            Users = [..users],
            Topics = new TVector<IForumTopic>()
        };
    }

    private static int NormalizeLimit(int limit)
    {
        return limit <= 0 ? 20 : Math.Min(limit, MaxSearchLimit);
    }

    /// <summary>
    /// Spends one free daily search, or charges Stars once the free quota is used up. Clients learn
    /// the price from <c>channels.checkSearchPostsFlood</c> and confirm it via <c>allow_paid_stars</c>.
    /// </summary>
    private async Task ChargeSearchAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestSearchPosts obj)
    {
        if (await SearchPostsFloodHelper.TryConsumeFreeSearchAsync(mongoDatabase, input.UserId))
        {
            return;
        }

        var price = SearchPostsFloodHelper.StarsAmount;

        // The client has to acknowledge the price before we may spend the user's Stars.
        if (obj.AllowPaidStars is null || obj.AllowPaidStars < price)
        {
            RpcErrors.RpcErrors400.StarsPaymentRequired.ThrowRpcError();
        }

        var balance = await StarsBalanceHelper.GetBalanceAsync(mongoDatabase, input.UserId);
        if (balance < price)
        {
            RpcErrors.RpcErrors400.BalanceTooLow.ThrowRpcError();
        }

        await StarsBalanceHelper.AddBalanceAsync(mongoDatabase, input.UserId, -price);
        await StarsBalanceHelper.AddTransactionAsync(mongoDatabase, input.UserId, -price);
    }

    private static string NormalizeQuery(string? query)
    {
        return query?.Trim() ?? string.Empty;
    }
}
