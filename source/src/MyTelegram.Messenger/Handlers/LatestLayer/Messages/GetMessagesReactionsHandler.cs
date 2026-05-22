namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetMessagesReactionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IUserConverterService userConverterService,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetMessagesReactions, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetMessagesReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

        // Only broadcast channels hide who reacted; supergroups/megagroups show reactions
        bool isBroadcast = false;
        if (peer.PeerType == PeerType.Channel)
        {
            var channel = await channelAppService.GetAsync(peer.PeerId);
            isBroadcast = channel?.Broadcast ?? false;
        }
        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ReactionReadState.GetKey(peer)));
        var readDate = ReactionReadState.ParseReadDate(readState?.Value);
        var updates = new List<IUpdate>();
        var allUserIds = new List<long>();

        foreach (var msgId in obj.Id)
        {
            var msg = await queryProcessor.ProcessAsync(new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, msgId)) as MessageReadModel;
            if (msg == null) continue;

            var recentReactions2 = msg.RecentReactions2 ?? [];

            var reactionCounts = (msg.Reactions ?? []).Select(r =>
            {
                // ChosenOrder: index among current user's reactions (bigger = newer per API spec)
                var userReactions = recentReactions2
                    .Where(rr => rr.SenderUserId == input.UserId)
                    .Select((rr, i) => (rr, i))
                    .ToList();
                var chosen = userReactions.FindIndex(x =>
                    x.rr.Reaction is TReactionEmoji e1 && r.Reaction is TReactionEmoji e2 && e1.Emoticon == e2.Emoticon ||
                    x.rr.Reaction is TReactionCustomEmoji c1 && r.Reaction is TReactionCustomEmoji c2 && c1.DocumentId == c2.DocumentId);
                return (IReactionCount)new TReactionCount
                {
                    Reaction = r.Reaction,
                    Count = r.Count,
                    ChosenOrder = chosen >= 0 ? userReactions[chosen].i : (int?)null
                };
            }).ToList();

            var isChannel = isBroadcast;

            var recentReactions = isChannel ? [] : recentReactions2.Select(r => (IMessagePeerReaction)new TMessagePeerReaction
            {
                PeerId = new TPeerUser { UserId = r.SenderUserId },
                Date = r.Date,
                Big = r.Big,
                My = r.SenderUserId == input.UserId,
                Unread = msg.SenderUserId == input.UserId && ReactionReadState.IsUnread(r, input.UserId, readDate),
                Reaction = r.Reaction
            }).ToList();

            if (!isChannel)
                allUserIds.AddRange(recentReactions2.Select(r => r.SenderUserId));

            updates.Add(new TUpdateMessageReactions
            {
                Peer = peer.ToPeer(),
                MsgId = msgId,
                Reactions = new TMessageReactions
                {
                    Results = new TVector<IReactionCount>(reactionCounts),
                    RecentReactions = recentReactions.Count > 0 ? new TVector<IMessagePeerReaction>(recentReactions) : null,
                    CanSeeList = !isChannel
                }
            });
        }

        var users = allUserIds.Count > 0
            ? await userConverterService.GetUserListAsync(input, allUserIds.Distinct().ToList(), false, false, input.Layer)
            : [];

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Users = new TVector<IUser>(users),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}
