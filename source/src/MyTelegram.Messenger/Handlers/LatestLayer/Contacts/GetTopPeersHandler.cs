namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Get most used peers
/// Possible errors
/// Code Type Description
/// 400 TYPES_EMPTY No top peer type was provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.getTopPeers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetTopPeersHandler(
    ITopPeerRatingService ratingService,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetTopPeers, MyTelegram.Schema.Contacts.ITopPeers>
{
    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    protected override async Task<ITopPeers> HandleCoreAsync(IRequestInput input, RequestGetTopPeers obj)
    {
        var requested = RequestedCategories(obj);
        if (requested.Count == 0)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        if (await ratingService.IsDisabledAsync(input.UserId))
        {
            return new TTopPeersDisabled();
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ratings = await ratingService.GetRatingsAsync(input.UserId, requested, now);

        var offset = Math.Max(0, obj.Offset);
        var limit = obj.Limit <= 0 ? DefaultLimit : Math.Min(obj.Limit, MaxLimit);

        var categories = new List<ITopPeerCategoryPeers>(requested.Count);
        var userIds = new HashSet<long>();
        var channelIds = new HashSet<long>();

        // Every requested category is answered, including the empty ones: tdlib clears its cached copy
        // only for the categories present in the response, so omitting one leaves its stale peers in the
        // vector tdlib hashes and topPeersNotModified can never match again.
        foreach (var category in requested)
        {
            var all = ratings[category];
            var page = all.Skip(offset).Take(limit).ToList();

            foreach (var item in page)
            {
                if (item.PeerType == PeerType.Channel)
                {
                    channelIds.Add(item.PeerId);
                }
                else
                {
                    userIds.Add(item.PeerId);
                }
            }

            categories.Add(new TTopPeerCategoryPeers
            {
                Category = TopPeerCategoryHelper.ToTl(category),
                // The total, not the page: clients show it as "and N more".
                Count = all.Count,
                Peers = new TVector<ITopPeer>(page.Select(p => (ITopPeer)new TTopPeer
                {
                    Peer = ToPeer(p),
                    Rating = p.Rating
                }))
            });
        }

        var hash = TopPeersHashHelper.ComputeHash(categories);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TTopPeersNotModified();
        }

        // The peers travel with the answer or they cannot be drawn at all — tdlib feeds users and chats
        // into its managers before reading the categories, and drops a top peer it cannot resolve.
        var channelMemberReadModels = channelIds.Count == 0
            ? []
            : await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(input.UserId, [.. channelIds]));

        var resultUsers = userIds.Count == 0
            ? []
            : await userConverterService.GetUserListAsync(input, [.. userIds], layer: input.Layer);
        var resultChats = channelIds.Count == 0
            ? []
            : await chatConverterService.GetChannelListAsync(input, [.. channelIds], channelMemberReadModels,
                layer: input.Layer);

        return new TTopPeers
        {
            Categories = new TVector<ITopPeerCategoryPeers>(categories),
            Chats = [.. resultChats],
            Users = [.. resultUsers]
        };
    }

    /// <summary>
    /// The requested categories in wire order. <c>bots_guestchat</c> (flags.17) is not part of layer 222,
    /// so a client asking for that alone gets <c>TYPES_EMPTY</c> — tdesktop's guest-bot strip does exactly
    /// that, iOS asks for it together with <c>bots_inline</c> and is unaffected.
    /// </summary>
    private static List<TopPeerCategory> RequestedCategories(RequestGetTopPeers obj)
    {
        var requested = new List<TopPeerCategory>(TopPeerCategoryHelper.WireOrder.Length);

        foreach (var category in TopPeerCategoryHelper.WireOrder)
        {
            var asked = category switch
            {
                TopPeerCategory.Correspondents => obj.Correspondents,
                TopPeerCategory.BotsPM => obj.BotsPm,
                TopPeerCategory.BotsInline => obj.BotsInline,
                TopPeerCategory.Groups => obj.Groups,
                TopPeerCategory.Channels => obj.Channels,
                TopPeerCategory.PhoneCalls => obj.PhoneCalls,
                TopPeerCategory.ForwardUsers => obj.ForwardUsers,
                TopPeerCategory.ForwardChats => obj.ForwardChats,
                TopPeerCategory.BotsApp => obj.BotsApp,
                _ => false
            };

            if (asked)
            {
                requested.Add(category);
            }
        }

        return requested;
    }

    private static IPeer ToPeer(TopPeerRating rating)
    {
        return rating.PeerType == PeerType.Channel
            ? new TPeerChannel { ChannelId = rating.PeerId }
            : new TPeerUser { UserId = rating.PeerId };
    }
}
