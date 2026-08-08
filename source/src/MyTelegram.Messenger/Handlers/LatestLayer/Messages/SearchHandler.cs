namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Search for messages.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 FROM_PEER_INVALID The specified from_id is invalid.
/// 400 INPUT_FILTER_INVALID The specified filter is invalid.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 PEER_ID_NOT_SUPPORTED The provided peer ID is not supported.
/// 400 SEARCH_QUERY_EMPTY The search query is empty.
/// 400 TAKEOUT_INVALID The specified takeout ID is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.search"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchHandler(IMessageAppService messageAppService, ITokenizer tokenizer, IPeerHelper peerHelper, IChannelAppService channelAppService, IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearch, IMessages>
{
    private const int MinTextSearchLength = 2;
    private const int MaxSearchLimit = 100;

    protected override async Task<IMessages> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSearch obj)
    {
        var q = NormalizeQuery(obj.Q);
        var messageTypes = MessageFilterHelper.GetMessageTypes(obj.Filter);
        var isPinned = MessageFilterHelper.IsPinnedFilter(obj.Filter);
        var myMentionsOnly = MessageFilterHelper.IsMyMentionsFilter(obj.Filter);
        var hasFilter = messageTypes.Count > 0 || isPinned || myMentionsOnly;

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

            q = string.Empty;
        }

        var userId = input.UserId;
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        var fromPeer = obj.FromId == null ? null : peerHelper.GetPeer(obj.FromId, userId);
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, userId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : userId;

        // Channel messages are stored with OwnerPeerId = channelId, and the search query filters on
        // that alone, so without a membership gate any user could read a private channel's history by
        // guessing its (sequential) id — the access hash on the input peer is not validated. This
        // mirrors the check messages.getHistory already performs.
        if (peer.PeerType == PeerType.Channel)
        {
            var channelReadModel = await channelAppService.GetAsync((long?)peer.PeerId);
            if (channelReadModel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, channelReadModel!))
            {
                return null!;
            }
        }

        var tokens = tokenizer.BuildSearchTokens(q);
        var limit = NormalizeLimit(obj.Limit);
        var getMessageOutput = await messageAppService.SearchAsync(new SearchInput
        {
            OwnerPeerId = ownerPeerId,
            SelfUserId = userId,
            Limit = limit,
            Q = q,
            OffsetId = obj.OffsetId,
            AddOffset = obj.AddOffset,
            Peer = peer,
            MaxDate = obj.MaxDate,
            MaxId = obj.MaxId,
            MinDate = obj.MinDate,
            MinId = obj.MinId,
            MessageType = isPinned ? MessageType.Pinned : MessageType.Unknown,
            MessageTypes = messageTypes,
            MyMentionsOnly = myMentionsOnly,
            Tokens = tokens,
            FilterSenderUserId = fromPeer?.PeerType == PeerType.User ? fromPeer.PeerId : 0,
            SavedPeerId = savedPeer,
            SavedReaction = savedPeer != null ? obj.SavedReaction : null,
            TopMsgId = obj.TopMsgId ?? 0
        });

        // hash covers the returned message ids so unchanged result sets can be short-circuited.
        // See https://corefork.telegram.org/api/offsets#hash-generation
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

    private static int NormalizeLimit(int limit)
    {
        return limit <= 0 ? 20 : Math.Min(limit, MaxSearchLimit);
    }

    private static string NormalizeQuery(string? query)
    {
        return query?.Trim() ?? string.Empty;
    }


}
