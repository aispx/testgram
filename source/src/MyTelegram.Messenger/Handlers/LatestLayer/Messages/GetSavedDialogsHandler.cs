namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
internal sealed class GetSavedDialogsHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedDialogs, MyTelegram.Schema.Messages.ISavedDialogs>
{
    protected override async Task<MyTelegram.Schema.Messages.ISavedDialogs> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedDialogs obj)
    {
        if (obj.ParentPeer != null)
        {
            await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
            var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer);
            var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
            if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum)
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

            var limit = obj.Limit > 0 ? obj.Limit : 20;
            var historyLimit = Math.Clamp(limit * 20, limit, 500);
            var history = await messageAppService.GetHistoryAsync(new GetHistoryInput
            {
                OwnerPeerId = monoforumPeer.PeerId,
                SelfUserId = input.UserId,
                AddOffset = 0,
                Limit = historyLimit,
                MaxId = obj.OffsetId,
                OffsetId = obj.OffsetId,
                Peer = monoforumPeer
            });

            var topMessages = history.MessageList
                .Where(m => m.SavedPeerId != null)
                .GroupBy(m => $"{m.SavedPeerId!.PeerType}:{m.SavedPeerId.PeerId}")
                .Select(g => g.OrderByDescending(m => m.MessageId).First())
                .OrderByDescending(m => m.MessageId)
                .Take(limit)
                .ToList();

            history.MessageList = topMessages;
            var converted = getHistoryConverterService.ToMessages(input, history, input.Layer);
            var (messageList, chatList, userList) = ExtractMessages(converted);

            var monoDialogs = topMessages.Select(m => new TMonoForumDialog
            {
                Peer = ToSchemaPeer(m.SavedPeerId!),
                TopMessage = m.MessageId,
                ReadInboxMaxId = 0,
                ReadOutboxMaxId = 0,
                UnreadCount = 0,
                UnreadReactionsCount = 0
            }).ToList<ISavedDialog>();

            return new TSavedDialogs
            {
                Dialogs = [.. monoDialogs],
                Messages = [.. messageList],
                Chats = [.. chatList],
                Users = [.. userList]
            };
        }

        return new TSavedDialogs { Chats = new TVector<IChat>(), Dialogs = [], Messages = new TVector<IMessage>(), Users = new TVector<IUser>() };
    }

    internal static (List<IMessage> Messages, List<IChat> Chats, List<IUser> Users) ExtractMessages(MyTelegram.Schema.Messages.IMessages converted)
    {
        return converted switch
        {
            TMessages tm => (tm.Messages.ToList(), tm.Chats.ToList(), tm.Users.ToList()),
            TChannelMessages tcm => (tcm.Messages.ToList(), tcm.Chats.ToList(), tcm.Users.ToList()),
            _ => ([], [], [])
        };
    }

    internal static IPeer ToSchemaPeer(Peer peer)
    {
        return peer.PeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        };
    }
}
