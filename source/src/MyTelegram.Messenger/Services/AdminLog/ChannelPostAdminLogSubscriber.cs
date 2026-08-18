using EventFlow.Subscribers;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.AdminLog;

/// <summary>
/// Records posts published in a channel in the
/// <a href="https://corefork.telegram.org/api/recent-actions">admin log</a>, which is what the <c>send</c>
/// flag of
/// <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">channelAdminLogEventsFilter</a>
/// selects.
/// <para>This cannot be done from the RPC handler: <c>messages.sendMessage</c> returns before the message
/// exists, it is created asynchronously by the aggregate, so the entry is written once the message really
/// has been created.</para>
/// </summary>
public sealed class ChannelPostAdminLogSubscriber(
    IMongoDatabase database,
    IChannelAppService channelAppService,
    IMessageConverterService messageConverterService,
    ILogger<ChannelPostAdminLogSubscriber> logger)
    : ISubscribeSynchronousTo<MessageAggregate, MessageId, OutboxMessageCreatedEvent>
{
    public async Task HandleAsync(
        IDomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var item = domainEvent.AggregateEvent.OutboxMessageItem;
        if (item.ToPeer.PeerType != PeerType.Channel)
        {
            return;
        }

        var channelReadModel = await channelAppService.GetAsync(item.ToPeer.PeerId);

        // Only broadcast channels have "posts"; messages sent in a supergroup are ordinary chat
        // traffic and are not part of the admin log.
        if (channelReadModel is not { Broadcast: true })
        {
            return;
        }

        try
        {
            var message = messageConverterService.ToMessage(item.SenderUserId, item);
            await AdminLogHelper.LogSendMessage(database, item.ToPeer.PeerId, item.SenderUserId, message);
        }
        catch (Exception e)
        {
            // The post itself is already stored; failing to log it must not fail message delivery.
            logger.LogError(e, "Failed to write the admin log entry for post {MessageId} in channel {ChannelId}",
                item.MessageId, item.ToPeer.PeerId);
        }
    }
}
