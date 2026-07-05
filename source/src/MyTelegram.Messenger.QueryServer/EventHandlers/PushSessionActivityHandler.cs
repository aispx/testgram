namespace MyTelegram.Messenger.QueryServer.EventHandlers;

/// <summary>
/// Feeds the push online-filter: every incoming messenger-query request means the client has a
/// live MTProto connection for <c>permAuthKeyId</c>, so we refresh the "online" marker. This lets
/// <c>PushNotificationEventHandler</c> suppress redundant FCM/APNS pushes while the client is
/// connected (matching upstream Telegram behaviour).
/// </summary>
public class PushSessionActivityHandler(IPushOnlineFilter onlineFilter, ILogger<PushSessionActivityHandler> logger)
    : IEventHandler<MessengerQueryDataReceivedEvent>, ITransientDependency
{
    public async Task HandleEventAsync(MessengerQueryDataReceivedEvent eventData)
    {
        // Only authenticated sessions carry a meaningful permAuthKeyId.
        if (eventData.PermAuthKeyId == 0 || eventData.UserId == 0)
        {
            return;
        }

        try
        {
            await onlineFilter.MarkOnlineAsync(eventData.PermAuthKeyId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to refresh online marker for permAuthKeyId={PermAuthKeyId}",
                eventData.PermAuthKeyId);
        }
    }
}
