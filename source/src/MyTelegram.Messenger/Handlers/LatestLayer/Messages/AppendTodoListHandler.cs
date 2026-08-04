using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Appends one or more items to a <a href="https://corefork.telegram.org/api/todo">todo list »</a>.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TODO_ITEM_DUPLICATE Duplicate <a href="https://corefork.telegram.org/api/todo">checklist items</a> detected.
/// 400 TODO_ITEM_INVALID A <a href="https://corefork.telegram.org/api/todo">checklist item</a> with a non-positive id was passed.
/// 400 TODO_ITEMS_TOO_MUCH Too many <a href="https://corefork.telegram.org/api/todo">checklist items</a>.
/// 400 TODO_NOT_MODIFIED No todo items were specified, so no changes were made to the todo list.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.appendTodoList"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AppendTodoListHandler(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IMessageAppService messageAppService,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestAppendTodoList, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestAppendTodoList obj)
    {
        if (obj.List.Count == 0)
        {
            RpcErrors.RpcErrors400.TodoNotModified.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;

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

        if (messageReadModel.SenderUserId != input.UserId && !messageMediaToDo.Todo.OthersCanAppend)
        {
            RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
        }

        TodoListHelper.ValidateAppendedItems(messageMediaToDo.Todo, obj.List);

        var todo = messageMediaToDo.Todo;
        todo.List = new TVector<ITodoItem>(todo.List.Concat(obj.List));

        await TodoUpdatePublisher.PublishToAllCopiesAsync(commandBus, queryProcessor, messageReadModel,
            input.ToRequestInfo(), todo, TodoMediaFactory.ToCompletionItems(messageMediaToDo.Completions));

        // Service message about appended tasks.
        var action = new TMessageActionTodoAppendTasks { List = obj.List };
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
}
