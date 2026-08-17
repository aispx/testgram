using EventFlow.Queries;
using Microsoft.Extensions.Options;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Queries;
using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.EventHandlers;

/// <summary>
/// Consumes <see cref="LayeredPushMessageCreatedIntegrationEvent"/> and delivers the embedded
/// <see cref="PushData"/> as a real PUSH notification to every registered device of the target
/// user, decryptable by official clients per <see href="https://corefork.telegram.org/api/push-updates">PUSH updates</see>.
/// <para>
/// This is the missing link: <c>DomainEventHandlerBase</c> already publishes these events with a
/// <c>PushData</c> payload, but until now nothing delivered them to FCM/APNS/Web-Push. Devices are
/// resolved via <see cref="GetPushDevicesForRecipientQuery"/> so multi-account clients
/// (<c>other_uids</c>) receive notifications addressed to any of their accounts; the sender's auth
/// key and any currently-online auth keys are skipped (matches upstream Telegram: an active MTProto
/// session gets the update directly, so the push is redundant).
/// </para>
/// <para>
/// Per-device behaviour (Req 3.4, 4.7, 7.1, 7.3, 9.2, 10.1, 10.2, 10.3, 12.1):
/// <list type="bullet">
/// <item>devices are de-duplicated by <c>Token</c> so at most one push is sent per unique token;</item>
/// <item>when the device's auth key has an active passcode lock and this is a new-message
///   notification, the payload is rewritten to <c>LOCKED_MESSAGE</c> with no message text in
///   <c>loc_args</c>;</item>
/// <item>the recipient account id is stamped into <c>user_id</c>;</item>
/// <item>when a provider reports the token as invalidated, an <see cref="UnRegisterDeviceCommand"/>
///   is published to remove the stale device;</item>
/// <item>each device is delivered inside its own try/catch so one failure never blocks the rest.</item>
/// </list>
/// </para>
/// </summary>
public class PushNotificationEventHandler(
    IQueryProcessor queryProcessor,
    IPushDispatcher dispatcher,
    IPushOnlineFilter onlineFilter,
    IDeviceLockStore deviceLockStore,
    ICommandBus commandBus,
    IMtpHelper mtpHelper,
    IAuthKeyIdHelper authKeyIdHelper,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<PushNotificationEventHandler> logger)
    : IEventHandler<LayeredPushMessageCreatedIntegrationEvent>, ITransientDependency
{
    /// <summary>
    /// loc_key prefixes that denote an incoming-message notification carrying message text in
    /// <c>loc_args</c>. When the device is locked these are rewritten to <c>LOCKED_MESSAGE</c>.
    /// </summary>
    private static readonly string[] NewMessageLocKeyPrefixes = ["MESSAGE_", "CHAT_MESSAGE_", "CHANNEL_MESSAGE_"];

    public async Task HandleEventAsync(LayeredPushMessageCreatedIntegrationEvent eventData)
    {
        var cfg = options.Value.Push;
        if (!cfg.Enabled || eventData.PushData is null)
        {
            return;
        }

        var recipientUserId = ResolveUserId(eventData);
        if (recipientUserId == 0)
        {
            return;
        }

        IReadOnlyCollection<IPushDeviceReadModel> devices;
        try
        {
            // Multi-account routing (Req 10.2): a device is addressable to the recipient when it is
            // owned by them or lists them in OtherUids.
            devices = await queryProcessor.ProcessAsync(new GetPushDevicesForRecipientQuery(recipientUserId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load push devices for user {UserId}", recipientUserId);
            return;
        }

        if (devices is null || devices.Count == 0)
        {
            return;
        }

        // Stamp the recipient account into user_id (Req 4.7, 10.1).
        var pushData = eventData.PushData with { UserId = recipientUserId };
        var isNewMessage = IsNewMessageNotification(pushData.LocKey);

        // Deduplicate by Token: never send more than one push per unique device token (Req 10.3).
        var sentTokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            // Don't push back to the device that originated the action (Req 7.3).
            if (eventData.ExcludeAuthKeyId.HasValue && eventData.ExcludeAuthKeyId.Value == device.PermAuthKeyId)
            {
                continue;
            }

            // A device-targeted update must not notify the recipient's other devices. Secret chats are
            // bound to one Authorization_Key, so the other devices hold no key for the chat: a push there
            // is undismissable noise and leaks the chat's existence and timing.
            if (eventData.OnlySendToThisAuthKeyId.HasValue &&
                eventData.OnlySendToThisAuthKeyId.Value != device.PermAuthKeyId)
            {
                continue;
            }

            // Skip devices with an active MTProto session: the update reaches them directly and a
            // push would be redundant (Req 7.1, battery-friendly, same as upstream).
            if (await onlineFilter.IsOnlineAsync(device.PermAuthKeyId))
            {
                continue;
            }

            // At most one push per unique token (Req 10.3). Reserve the token only for devices that
            // actually pass the exclude/online filters so an excluded device never "consumes" it.
            if (string.IsNullOrEmpty(device.Token) || !sentTokens.Add(device.Token))
            {
                continue;
            }

            try
            {
                // Hide message text when the device is passcode-locked (Req 9.2). Only applies to
                // new-message notifications; service/cancel pushes (deletes, read-history, ...) and
                // calls pass through unchanged.
                var deviceData = pushData;
                if (isNewMessage && await deviceLockStore.IsLockedAsync(device.PermAuthKeyId))
                {
                    deviceData = pushData with
                    {
                        LocKey = PushNotificationTypes.LockedMessage,
                        LocArgs = []
                    };
                }

                var payload = PushPayloadEncryptor.EncryptForDevice(device.Secret, deviceData, mtpHelper, authKeyIdHelper);
                var outcome = await dispatcher.SendAsync(device, payload);

                if (outcome == PushSendOutcome.TokenInvalidated)
                {
                    // The provider reported the token as no longer valid (APNs 410, FCM 404
                    // UNREGISTERED). Remove the stale device (Req 3.4).
                    await UnregisterStaleDeviceAsync(device);
                    logger.LogInformation("Push token invalidated, device unregistered: user={UserId} type={TokenType}",
                        recipientUserId, device.TokenType);
                }
                else
                {
                    logger.LogInformation("Push delivered: user={UserId} type={TokenType} locKey={LocKey} outcome={Outcome}",
                        recipientUserId, device.TokenType, deviceData.LocKey, outcome);
                }
            }
            catch (Exception ex)
            {
                // Fault isolation (Req 12.1): a failure on one device must not stop delivery to the
                // remaining devices of the same recipient.
                logger.LogWarning(ex, "Push delivery failed: user={UserId} type={TokenType} locKey={LocKey}",
                    recipientUserId, device.TokenType, pushData.LocKey);
            }
        }
    }

    private async Task UnregisterStaleDeviceAsync(IPushDeviceReadModel device)
    {
        var otherUids = device.OtherUids is { Count: > 0 }
            ? new List<long>(device.OtherUids)
            : new List<long>();

        var command = new UnRegisterDeviceCommand(
            PushDeviceId.Create(device.Token, device.UserId),
            RequestInfo.Empty,
            device.TokenType,
            device.Token,
            otherUids);

        await commandBus.PublishAsync(command, CancellationToken.None);
    }

    private static bool IsNewMessageNotification(string locKey)
    {
        if (string.IsNullOrEmpty(locKey))
        {
            return false;
        }

        // MESSAGE_DELETED is a service/cancel notification, not an incoming message.
        if (locKey == PushNotificationTypes.MessageDeleted)
        {
            return false;
        }

        foreach (var prefix in NewMessageLocKeyPrefixes)
        {
            if (locKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static long ResolveUserId(LayeredPushMessageCreatedIntegrationEvent eventData)
    {
        // PushData.UserId is set when the producer already knew the recipient. Otherwise infer it
        // from the peer (User peer => that user; Chat/Channel => use OnlySendToUserId which the
        // producer passes for single-member pushes).
        if (eventData.PushData!.UserId != 0)
        {
            return eventData.PushData.UserId;
        }

        if (eventData.PeerType == PeerType.User)
        {
            return eventData.PeerId;
        }

        return eventData.OnlySendToUserId ?? 0;
    }
}
