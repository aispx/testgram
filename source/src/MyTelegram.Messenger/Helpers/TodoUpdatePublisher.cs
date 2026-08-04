using MyTelegram.Domain.Aggregates.Messaging;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Applies a <a href="https://corefork.telegram.org/api/todo">todo list »</a> change to every copy
/// of the message carrying the checklist.
/// </summary>
/// <remarks>
/// In a private chat the sender and the recipient each own a separate message aggregate with its own
/// id, linked by <c>BatchId</c>. A checklist is collaborative, so ticking an item must be reflected
/// in both copies — otherwise the two sides would disagree about the state of the list.
/// Channels and groups share a single message, so only one copy exists there.
/// </remarks>
internal static class TodoUpdatePublisher
{
    public static async Task PublishToAllCopiesAsync(
        ICommandBus commandBus,
        IQueryProcessor queryProcessor,
        IMessageReadModel messageReadModel,
        RequestInfo requestInfo,
        ITodoList todo,
        List<TodoCompletionItem> completions)
    {
        await PublishAsync(commandBus, messageReadModel.OwnerPeerId, messageReadModel.MessageId, requestInfo, todo,
            completions);

        if (messageReadModel.ToPeerType != PeerType.User || messageReadModel.BatchId == Guid.Empty)
        {
            return;
        }

        var counterpart = await queryProcessor.ProcessAsync(
            new GetMessageByBatchIdQuery(messageReadModel.BatchId, messageReadModel.OwnerPeerId));
        if (counterpart == null)
        {
            return;
        }

        await PublishAsync(commandBus, counterpart.OwnerPeerId, counterpart.MessageId, requestInfo, todo, completions);
    }

    private static async Task PublishAsync(
        ICommandBus commandBus,
        long ownerPeerId,
        int messageId,
        RequestInfo requestInfo,
        ITodoList todo,
        List<TodoCompletionItem> completions)
    {
        var command = new UpdateTodoListCommand(
            MessageId.Create(ownerPeerId, messageId),
            requestInfo,
            todo,
            completions);

        try
        {
            await commandBus.PublishAsync(command);
        }
        catch (Exception ex) when (ex.Message.Contains("AggregateIsCreatedSpecification"))
        {
            // The message exists in the read model but its aggregate was never created
            // (pre-existing message) — nothing to update. Same guard as SendReactionHandler.
        }
    }
}
