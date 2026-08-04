using MongoDB.Driver;

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
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    IQueryProcessor queryProcessor)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetTopPeers, MyTelegram.Schema.Contacts.ITopPeers>
{
    private const int MaxLimit = 100;

    protected override async Task<ITopPeers> HandleCoreAsync(IRequestInput input, RequestGetTopPeers obj)
    {
        if (!obj.Correspondents && !obj.BotsPm && !obj.BotsInline && !obj.BotsApp && !obj.PhoneCalls &&
            !obj.ForwardUsers && !obj.ForwardChats && !obj.Groups && !obj.Channels)
        {
            RpcErrors.RpcErrors400.TypesEmpty.ThrowRpcError();
        }

        if (await TopPeerRatingHelper.IsDisabledAsync(mongoDatabase, input.UserId))
        {
            return new TTopPeersDisabled();
        }

        var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ratings = await TopPeerRatingHelper.GetRatingsAsync(mongoDatabase, input.UserId, now);
        if (ratings.Count == 0)
        {
            return new TTopPeers
            {
                Categories = new TVector<ITopPeerCategoryPeers>(),
                Chats = new TVector<IChat>(),
                Users = new TVector<IUser>()
            };
        }

        // Bot flags live on the user read model, so user peers have to be resolved before they can
        // be split between the correspondents/bots categories.
        var userIds = ratings.Where(p => p.PeerType is PeerType.User or PeerType.Self)
            .Select(p => p.PeerId)
            .ToList();
        var users = await userAppService.GetListAsync(userIds);
        var userMap = users.ToDictionary(p => p.UserId);

        var channelIds = ratings.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId).ToList();
        var channels = await channelAppService.GetListAsync(channelIds);
        var channelMap = channels.ToDictionary(p => p.ChannelId);

        var offset = Math.Max(0, obj.Offset);
        var limit = obj.Limit <= 0 ? 20 : Math.Min(obj.Limit, MaxLimit);

        var categories = new List<ITopPeerCategoryPeers>();
        var includedUserIds = new HashSet<long>();
        var includedChannelIds = new HashSet<long>();

        void AddCategory(ITopPeerCategory category, IEnumerable<TopPeerRatingHelper.PeerRating> source)
        {
            var matching = source.ToList();
            if (matching.Count == 0)
            {
                return;
            }

            var page = matching.Skip(offset).Take(limit).ToList();
            if (page.Count == 0)
            {
                return;
            }

            foreach (var item in page)
            {
                switch (item.PeerType)
                {
                    case PeerType.Channel:
                        includedChannelIds.Add(item.PeerId);
                        break;

                    case PeerType.User:
                    case PeerType.Self:
                        includedUserIds.Add(item.PeerId);
                        break;
                }
            }

            categories.Add(new TTopPeerCategoryPeers
            {
                Category = category,
                Count = matching.Count,
                Peers = new TVector<ITopPeer>(page.Select(p => (ITopPeer)new TTopPeer
                {
                    Peer = ToPeer(p),
                    Rating = p.Rating
                }))
            });
        }

        bool IsBot(TopPeerRatingHelper.PeerRating rating)
        {
            return userMap.TryGetValue(rating.PeerId, out var user) && user.Bot;
        }

        var userRatings = ratings.Where(p => p.PeerType is PeerType.User or PeerType.Self).ToList();

        if (obj.Correspondents)
        {
            AddCategory(new TTopPeerCategoryCorrespondents(), userRatings.Where(p => !IsBot(p)));
        }

        if (obj.BotsPm)
        {
            AddCategory(new TTopPeerCategoryBotsPM(), userRatings.Where(IsBot));
        }

        if (obj.BotsInline)
        {
            // There is no stored "supports inline queries" flag, so inline bots cannot be told
            // apart from other bots here; the client filters them further by bot info.
            AddCategory(new TTopPeerCategoryBotsInline(), userRatings.Where(IsBot));
        }

        if (obj.BotsApp)
        {
            AddCategory(new TTopPeerCategoryBotsApp(),
                userRatings.Where(p => userMap.TryGetValue(p.PeerId, out var user) && user is { Bot: true, BotHasMainApp: true }));
        }

        if (obj.PhoneCalls)
        {
            AddCategory(new TTopPeerCategoryPhoneCalls(), userRatings.Where(p => p.IsPhoneCall));
        }

        if (obj.ForwardUsers)
        {
            AddCategory(new TTopPeerCategoryForwardUsers(), userRatings.Where(p => p.IsForward));
        }

        if (obj.ForwardChats)
        {
            AddCategory(new TTopPeerCategoryForwardChats(),
                ratings.Where(p => p.PeerType is PeerType.Chat or PeerType.Channel && p.IsForward));
        }

        if (obj.Groups)
        {
            AddCategory(new TTopPeerCategoryGroups(), ratings.Where(IsGroup));
        }

        if (obj.Channels)
        {
            AddCategory(new TTopPeerCategoryChannels(),
                ratings.Where(p => p.PeerType == PeerType.Channel &&
                                   channelMap.TryGetValue(p.PeerId, out var channel) && channel.Broadcast));
        }

        // hash lets clients skip an unchanged rating. See https://corefork.telegram.org/api/offsets#hash-generation
        var hash = categories
            .SelectMany(p => ((TTopPeerCategoryPeers)p).Peers)
            .Aggregate(0L, (current, peer) => Messages.MessageSearchMongoHelper.CalcHash(current,
                ((TTopPeer)peer).Peer.ToPeerId() ?? 0));
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TTopPeersNotModified();
        }

        var channelMemberReadModels = includedChannelIds.Count == 0
            ? []
            : await queryProcessor.ProcessAsync(
                new GetChannelMemberListByChannelIdListQuery(input.UserId, [.. includedChannelIds]));

        var resultUsers = includedUserIds.Count == 0
            ? []
            : await userConverterService.GetUserListAsync(input, [.. includedUserIds], layer: input.Layer);
        var resultChats = includedChannelIds.Count == 0
            ? []
            : await chatConverterService.GetChannelListAsync(input, [.. includedChannelIds], channelMemberReadModels,
                layer: input.Layer);

        return new TTopPeers
        {
            Categories = new TVector<ITopPeerCategoryPeers>(categories),
            Chats = [.. resultChats],
            Users = [.. resultUsers]
        };

        bool IsGroup(TopPeerRatingHelper.PeerRating rating)
        {
            return rating.PeerType switch
            {
                PeerType.Chat => true,
                PeerType.Channel => channelMap.TryGetValue(rating.PeerId, out var channel) && channel.MegaGroup,
                _ => false
            };
        }
    }

    private static IPeer ToPeer(TopPeerRatingHelper.PeerRating rating)
    {
        return rating.PeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = rating.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = rating.PeerId },
            _ => new TPeerUser { UserId = rating.PeerId }
        };
    }
}
