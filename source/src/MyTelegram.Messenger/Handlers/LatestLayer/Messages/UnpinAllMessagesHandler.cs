using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// <a href="https://corefork.telegram.org/api/pin">Unpin</a> all pinned messages
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.unpinAllMessages"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class UnpinAllMessagesHandler(ICommandBus commandBus, IPeerHelper peerHelper, IPinRightsChecker pinRightsChecker, IPtsHelper ptsHelper, IQueryProcessor queryProcessor, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUnpinAllMessages, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestUnpinAllMessages obj)
    {
        // Without the self user id inputPeerSelf resolves to peer id 0, and unpinning in Saved Messages
        // would silently match nothing.
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null || peer.PeerType == PeerType.Empty)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await pinRightsChecker.CheckPinRightsAsync(input, peer!);

        var ownerPeerId = peer!.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, input.UserId);

        var messageItems = await queryProcessor.ProcessAsync(new GetSimpleMessageListQuery(ownerPeerId, peer, null,
            true, true, MyTelegramConsts.UnPinAllMessagesDefaultPageSize, obj.TopMsgId, savedPeer));
        if (messageItems.Count == 0)
        {
            return new TAffectedHistory
            {
                Pts = ptsHelper.GetCachedPts(ownerPeerId),
                PtsCount = 0,
                Offset = 0
            };
        }

        // A full page means more pinned messages may be left; the saga turns this into the
        // affectedHistory offset the client loops on.
        var lastBatch = PinPagingHelper.IsLastBatch(messageItems.Count);

        var command = new StartUnpinAllMessagesCommand(TempId.New, input.ToRequestInfo(), messageItems, peer, lastBatch);
        await commandBus.PublishAsync(command);

        if (peer.PeerType == PeerType.Channel)
        {
            await LogUnpinAsync(input, peer, messageItems);
        }

        return null!;
    }

    /// <summary>
    /// Recent actions show every unpin, not only the ones made one by one through
    /// messages.updatePinnedMessage. The <c>pinned</c> flag left unset is what marks the entry as an unpin.
    /// </summary>
    private async Task LogUnpinAsync(IRequestInput input, Peer peer, IReadOnlyCollection<SimpleMessageItem> messageItems)
    {
        foreach (var item in messageItems)
        {
            var message = new TMessage
            {
                Id = item.MessageId,
                PeerId = new TPeerChannel { ChannelId = peer.PeerId },
                Message = string.Empty,
                Date = CurrentDate,
                Pinned = false,
                Media = new TMessageMediaEmpty(),
                Entities = new TVector<IMessageEntity>()
            };

            await AdminLogHelper.LogUpdatePinned(mongoDatabase, peer.PeerId, input.UserId, message);
        }
    }
}