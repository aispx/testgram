namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark one or more items of a <a href="https://corefork.telegram.org/api/todo">todo list »</a> as completed or not completed.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.toggleTodoCompleted"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleTodoCompletedHandler(IQueryProcessor queryProcessor, ICommandBus commandBus, IAccessHashHelper accessHashHelper, IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestToggleTodoCompleted, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestToggleTodoCompleted obj)
    {
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
            if (messageReadModel.SenderUserId != input.UserId && !messageMediaToDo.Todo.OthersCanComplete)
            {
                RpcErrors.RpcErrors403.ChatWriteForbidden.ThrowRpcError();
            }

            var completions = messageMediaToDo.Completions?.ToList() ?? new List<ITodoCompletion>();
            var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Mark items as completed
            foreach (var itemId in obj.Completed)
            {
                // Remove from completions if already there (toggle)
                var existing = completions.FirstOrDefault(c => c.Id == itemId);
                if (existing == null)
                {
                    completions.Add(new TTodoCompletion
                    {
                        Id = itemId,
                        CompletedBy = new TPeerUser { UserId = input.UserId },
                        Date = currentDate
                    });
                }
            }

            // Mark items as not completed (remove from completions)
            foreach (var itemId in obj.Incompleted)
            {
                var existing = completions.FirstOrDefault(c => c.Id == itemId);
                if (existing != null)
                {
                    completions.Remove(existing);
                }
            }

            messageMediaToDo.Completions = new TVector<ITodoCompletion>(completions);
        }

        var command = new EditOutboxMessageCommand(MessageId.Create(ownerPeerId, obj.MsgId), input.ToRequestInfo(), obj.MsgId, string.Empty, CurrentDate, null, media, null, false, null);
        await commandBus.PublishAsync(command);

        // Send service message about completion changes
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
            messageAction: action
        );
        await messageAppService.SendMessageAsync([sendInput]);

        return null!;
    }
}