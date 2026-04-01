namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Appends one or more items to a <a href="https://corefork.telegram.org/api/todo">todo list »</a>.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 TODO_ITEM_DUPLICATE Duplicate <a href="https://corefork.telegram.org/api/todo">checklist items</a> detected.
/// 400 TODO_NOT_MODIFIED No todo items were specified, so no changes were made to the todo list.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.appendTodoList"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AppendTodoListHandler(IQueryProcessor queryProcessor, ICommandBus commandBus, IAccessHashHelper accessHashHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestAppendTodoList, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestAppendTodoList obj)
    {
        const int TODO_ITEM_LENGTH_MAX = 200;
        const int TODO_ITEMS_MAX = 30;

        if (obj.List.Count == 0)
        {
            RpcErrors.RpcErrors400.TodoNotModified.ThrowRpcError();
        }

        await accessHashHelper.CheckAccessHashAsync(input, obj.Peer);
        var peer = obj.Peer.ToPeer(input.UserId);
        var ownerPeerId = peer.PeerId;
        if (peer.PeerType != PeerType.Channel)
        {
            ownerPeerId = input.UserId;
        }

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.MsgId).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var media = messageReadModel!.Media2;
        if (media is TMessageMediaToDo messageMediaToDo)
        {
            if (messageReadModel.SenderUserId != input.UserId && !messageMediaToDo.Todo.OthersCanAppend)
            {
                RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
            }

            // Check total items limit
            if (messageMediaToDo.Todo.List.Count + obj.List.Count > TODO_ITEMS_MAX)
            {
                RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
            }

            // Check for duplicate IDs
            var existingIds = messageMediaToDo.Todo.List.Select(x => x.Id).ToHashSet();
            foreach (var todoItem in obj.List)
            {
                if (todoItem.Id <= 0)
                {
                    RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
                }

                if (existingIds.Contains(todoItem.Id))
                {
                    RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
                }

                if (todoItem.Title.Text.Length > TODO_ITEM_LENGTH_MAX)
                {
                    RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
                }

                messageMediaToDo.Todo.List.Add(todoItem);
            }
        }

        var command = new EditOutboxMessageCommand(MessageId.Create(ownerPeerId, obj.MsgId), input.ToRequestInfo(), obj.MsgId, string.Empty, CurrentDate, null, media, null, false, null);
        await commandBus.PublishAsync(command);
        return null !;
    }
}