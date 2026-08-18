namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get messages in a reply thread
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TOPIC_ID_INVALID The specified topic ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getReplies"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRepliesHandler(
    IPeerHelper peerHelper,
    IMessageAppService messageAppService,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IGetHistoryConverterService getHistoryConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetReplies, MyTelegram.Schema.Messages.IMessages>
{
    /// <summary>Upper bound on one thread page, matching messages.getHistory and the search handlers.</summary>
    private const int MaxRepliesLimit = 100;

    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, RequestGetReplies obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // A reply thread lives inside a channel/supergroup and is readable only by members, exactly
        // like messages.getHistory. GetPeer validates no access hash and channel ids are sequential,
        // so resolving the peer proves nothing — gate membership explicitly, otherwise a non-member
        // reads a private channel's comment thread.
        if (peer.PeerType == PeerType.Channel)
        {
            var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(peer.PeerId, input.UserId));
            if (channelMember?.Kicked == true)
            {
                return new TChannelMessages
                {
                    Chats = new TVector<IChat>(),
                    Messages = new TVector<IMessage>(),
                    Users = new TVector<IUser>()
                };
            }

            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
            {
                return null!;
            }
        }

        // The read-model store only applies a Mongo limit when it is > 0, so limit=0 or a negative
        // value meant "no limit at all" and returned the whole thread in one response.
        var limit = obj.Limit is <= 0 or > MaxRepliesLimit ? MaxRepliesLimit : obj.Limit;

        var getMessageOutput = await messageAppService.GetRepliesAsync(new GetRepliesInput
        {
            ReplyToMsgId = obj.MsgId,
            OwnerPeerId = peer.PeerId,
            AddOffset = obj.AddOffset,
            Limit = limit,
            OffsetId = obj.OffsetId,
            // offset_date is an upper bound ("only messages older than this date"), exactly as in
            // messages.getHistory - reading it as a lower bound paged the thread in the wrong direction.
            MaxDate = obj.OffsetDate,
            MaxId = obj.MaxId,
            MinId = obj.MinId,
            SelfUserId = input.UserId
        });

        // The hash covers the returned message ids, so a client re-reading an unchanged page gets
        // nothing back. See https://corefork.telegram.org/api/offsets#hash-generation
        if (obj.Hash != 0)
        {
            var hash = getMessageOutput.MessageList.Aggregate(0L,
                (current, message) => MessageSearchMongoHelper.CalcHash(current, message.MessageId));
            if (hash == obj.Hash)
            {
                return new TMessagesNotModified { Count = getMessageOutput.MessageList.Count };
            }
        }

        return getHistoryConverterService.ToMessages(input, getMessageOutput, input.Layer);
    }
}
