namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Obtain information about specific <a href="https://corefork.telegram.org/api/saved-messages#saved-message-dialogs">saved message dialogs »</a> or <a href="https://corefork.telegram.org/api/monoforum">monoforum topics »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getSavedDialogsByID"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedDialogsByIDHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedDialogsByID, MyTelegram.Schema.Messages.ISavedDialogs>
{
    protected override async Task<MyTelegram.Schema.Messages.ISavedDialogs> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedDialogsByID obj)
    {
        if (obj.ParentPeer == null)
            return new TSavedDialogs { Chats = new TVector<IChat>(), Dialogs = [], Messages = new TVector<IMessage>(), Users = new TVector<IUser>() };

        await accessHashHelper.CheckAccessHashAsync(input, obj.ParentPeer);
        var monoforumPeer = peerHelper.GetPeer(obj.ParentPeer);
        var monoforumReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(monoforumPeer.PeerId));
        if (monoforumReadModel == null || !monoforumReadModel.IsMonoforum)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var topMessages = new List<IMessageReadModel>();
        foreach (var id in obj.Ids)
        {
            await accessHashHelper.CheckAccessHashAsync(input, id);
            var topicPeer = peerHelper.GetPeer(id, input.UserId);
            var history = await messageAppService.GetHistoryAsync(new GetHistoryInput
            {
                OwnerPeerId = monoforumPeer.PeerId,
                SelfUserId = input.UserId,
                Limit = 1,
                Peer = monoforumPeer,
                SavedPeerId = topicPeer
            });

            var topMessage = history.MessageList.OrderByDescending(m => m.MessageId).FirstOrDefault();
            if (topMessage != null)
            {
                topMessages.Add(topMessage);
            }
        }

        if (topMessages.Count == 0)
            return new TSavedDialogs { Chats = new TVector<IChat>(), Dialogs = [], Messages = new TVector<IMessage>(), Users = new TVector<IUser>() };

        var output = await messageAppService.GetChannelDifferenceAsync(new GetDifferenceInput(
            input.UserId,
            monoforumPeer.PeerId,
            0,
            topMessages.Count,
            topMessages.Select(m => m.MessageId).ToList()));
        output.MessageList = topMessages;
        var converted = getHistoryConverterService.ToMessages(input, output, input.Layer);
        var (messageList, chatList, userList) = GetSavedDialogsHandler.ExtractMessages(converted);

        var monoDialogs = topMessages.Select(m => new TMonoForumDialog
        {
            Peer = GetSavedDialogsHandler.ToSchemaPeer(m.SavedPeerId!),
            TopMessage = m.MessageId,
            ReadInboxMaxId = 0,
            ReadOutboxMaxId = 0,
            UnreadCount = 0,
            UnreadReactionsCount = 0
        }).ToList<ISavedDialog>();

        return new TSavedDialogs
        {
            Chats = [.. chatList],
            Dialogs = [.. monoDialogs],
            Messages = [.. messageList],
            Users = [.. userList]
        };
    }
}
