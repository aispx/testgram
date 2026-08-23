using MyTelegram.Domain.Aggregates.Messaging;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Records the receipt message on every copy of a paid invoice.
/// </summary>
/// <remarks>
/// In a private chat the bot and the buyer each own a separate message aggregate with its own id,
/// linked by <c>BatchId</c>; both have to learn about the receipt or the two sides would disagree
/// about whether the invoice is still payable. Channels and groups share a single message, so only
/// one copy exists there. Same shape as <see cref="TodoUpdatePublisher"/>.
/// See https://corefork.telegram.org/api/payments#5-checkout
/// </remarks>
internal static class InvoiceReceiptPublisher
{
    public static async Task PublishToAllCopiesAsync(
        ICommandBus commandBus,
        IQueryProcessor queryProcessor,
        IMessageReadModel invoiceMessage,
        RequestInfo requestInfo,
        int receiptMsgId)
    {
        await PublishAsync(commandBus, invoiceMessage.OwnerPeerId, invoiceMessage.MessageId, requestInfo, receiptMsgId);

        if (invoiceMessage.ToPeerType != PeerType.User || invoiceMessage.BatchId == Guid.Empty)
        {
            return;
        }

        var counterpart = await queryProcessor.ProcessAsync(
            new GetMessageByBatchIdQuery(invoiceMessage.BatchId, invoiceMessage.OwnerPeerId));
        if (counterpart == null)
        {
            return;
        }

        await PublishAsync(commandBus, counterpart.OwnerPeerId, counterpart.MessageId, requestInfo, receiptMsgId);
    }

    private static async Task PublishAsync(
        ICommandBus commandBus,
        long ownerPeerId,
        int messageId,
        RequestInfo requestInfo,
        int receiptMsgId)
    {
        var command = new UpdateInvoiceReceiptCommand(
            MessageId.Create(ownerPeerId, messageId),
            requestInfo,
            receiptMsgId);

        try
        {
            await commandBus.PublishAsync(command);
        }
        catch (Exception ex) when (ex.Message.Contains("AggregateIsCreatedSpecification"))
        {
            // The message exists in the read model but its aggregate was never created
            // (pre-existing message) — nothing to update. Same guard as TodoUpdatePublisher.
        }
    }
}
