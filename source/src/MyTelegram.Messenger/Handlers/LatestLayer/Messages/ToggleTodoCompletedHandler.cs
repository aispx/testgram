using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark one or more items of a <a href="https://corefork.telegram.org/api/todo">todo list »</a> as completed or not completed.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleTodoCompleted"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleTodoCompletedHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleTodoCompleted, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestToggleTodoCompleted obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        if (peer.PeerType == PeerType.Channel)
        {
            var membershipChannel = await channelAppService.GetAsync((long?)peer.PeerId);
            if (membershipChannel == null)
            {
                RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            }

            // Toggling a checklist item writes into the channel and posts a service message, so it
            // requires membership even when others_can_complete is set. GetPeer validates no access
            // hash.
            if (await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, membershipChannel!))
            {
                return null!;
            }
        }

        var messageReadModel = await queryProcessor.ProcessAsync(
            new GetMessageByPeerIdAndMessageIdQuery(ownerPeerId, obj.MsgId));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        if (messageReadModel!.Media2 is not TMessageMediaToDo messageMediaToDo)
        {
            // Not a checklist — refuse instead of silently rewriting an unrelated message.
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return null!;
        }

        if (messageReadModel.SenderUserId != input.UserId && !messageMediaToDo.Todo.OthersCanComplete)
        {
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        var completions = TodoMediaFactory.ToCompletionItems(messageMediaToDo.Completions);
        var completedBy = await ResolveCompletedByAsync(input.UserId, peer);
        var currentDate = CurrentDate;

        // Only ids that actually exist in the list may be completed; TDLib drops unknown
        // completions when parsing messageMediaToDo, so accepting them would desync the clients.
        var knownIds = messageMediaToDo.Todo.List.Select(p => p.Id).ToHashSet();

        var changed = false;
        foreach (var itemId in obj.Completed)
        {
            if (!knownIds.Contains(itemId) || completions.Any(p => p.Id == itemId))
            {
                continue;
            }

            completions.Add(new TodoCompletionItem(itemId, completedBy, currentDate));
            changed = true;
        }

        foreach (var itemId in obj.Incompleted)
        {
            changed |= completions.RemoveAll(p => p.Id == itemId) > 0;
        }

        // The update is published even for a no-op so the caller always gets its RPC reply, which is
        // delivered from the domain event handler. TODO_NOT_MODIFIED is only documented for
        // messages.appendTodoList, so a no-op here is not an error — it just emits no service message.
        await TodoUpdatePublisher.PublishToAllCopiesAsync(commandBus, queryProcessor, messageReadModel,
            input.ToRequestInfo(), messageMediaToDo.Todo, completions);

        if (!changed)
        {
            return null!;
        }

        // Service message about completion changes, attributed to the same peer as the completions.
        var action = new TMessageActionTodoCompletions
        {
            Completed = obj.Completed,
            Incompleted = obj.Incompleted
        };
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            peer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action,
            inputReplyTo: new TInputReplyToMessage { ReplyToMsgId = obj.MsgId }
        );
        await messageAppService.SendMessageAsync([sendInput]);

        return null!;
    }

    /// <summary>
    /// Completions are attributed to the acting user, except for anonymous group admins, whose
    /// completions are attributed to the group itself — see
    /// https://corefork.telegram.org/api/todo (layer 217+).
    /// </summary>
    private async Task<Peer> ResolveCompletedByAsync(long userId, Peer peer)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return userId.ToUserPeer();
        }

        var sendAs = await messageAppService.GetAnonymousSendAsPeerAsync(peer.PeerId, userId);

        return sendAs ?? userId.ToUserPeer();
    }
}
