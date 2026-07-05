namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetUnreadReactionsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetUnreadReactions, MyTelegram.Schema.Messages.IMessages>
{
    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetUnreadReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var readState = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ReactionReadState.GetKey(peer, obj.TopMsgId, savedPeer)));
        var readDate = ReactionReadState.ParseReadDate(readState?.Value);

        var limit = obj.Limit > 0 && obj.Limit <= 100 ? obj.Limit : 20;
        var messages = await queryProcessor.ProcessAsync(
            new GetMessagesWithUnreadReactionsQuery(
                ownerPeerId,
                input.UserId,
                obj.OffsetId,
                limit,
                obj.MaxId,
                obj.MinId,
                readDate,
                obj.TopMsgId,
                savedPeer));

        var msgList = messages.Select(m =>
        {
            var recentReactions2 = (m as MessageReadModel)?.RecentReactions2 ?? [];
            var reactionCounts = ((m as MessageReadModel)?.Reactions ?? []).Select(r => (IReactionCount)new TReactionCount
            {
                Reaction = r.Reaction,
                Count = r.Count
            }).ToList();
            var recentReactions = recentReactions2.Select(r => (IMessagePeerReaction)new TMessagePeerReaction
            {
                PeerId = new TPeerUser { UserId = r.SenderUserId },
                Date = r.Date,
                My = r.SenderUserId == input.UserId,
                Unread = ReactionReadState.IsUnread(r, input.UserId, readDate),
                Reaction = r.Reaction
            }).ToList();

            return (IMessage)new TMessage
            {
                Id = m.MessageId,
                PeerId = peer.ToPeer(),
                SavedPeerId = savedPeer?.ToPeer(),
                Date = m.Date,
                Message = "",
                Reactions = new TMessageReactions
                {
                    Results = new TVector<IReactionCount>(reactionCounts),
                    RecentReactions = recentReactions.Count > 0 ? new TVector<IMessagePeerReaction>(recentReactions) : null,
                    CanSeeList = true
                }
            };
        }).ToList();

        return new TMessages
        {
            Messages = new TVector<IMessage>(msgList),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Topics = new TVector<IForumTopic>()
        };
    }
}
