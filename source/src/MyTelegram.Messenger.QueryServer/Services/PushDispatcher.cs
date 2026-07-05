using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Routes a push payload to the correct provider sender based on
/// <see cref="IPushDeviceReadModel.TokenType"/>. Mirrors the token-type table in
/// <see href="https://corefork.telegram.org/api/push-updates"/>.
/// </summary>
public class PushDispatcher(
    IPushFcmSender fcm,
    IPushApnsSender apns,
    IPushWebPushSender webPush,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<PushDispatcher> logger) : IPushDispatcher, ITransientDependency
{
    public async Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
    {
        var cfg = options.Value.Push;
        try
        {
            return device.TokenType switch
            {
                PushTokenType.Fcm when cfg.Fcm.Enabled => await fcm.SendAsync(device, base64Payload),
                PushTokenType.Apns when cfg.Apns.Enabled => await apns.SendAsync(device, base64Payload),
                PushTokenType.ApnsVoip when cfg.Apns.Enabled => await apns.SendAsync(device, base64Payload),
                PushTokenType.WebPush when cfg.WebPush.Enabled => await webPush.SendAsync(device, base64Payload),
                // MPNS/WNS/Ubuntu/BlackBerry/Tizen are not implemented; log once at debug.
                _ => LogUnsupported(device)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Push dispatcher error type={Type} token={Token}",
                device.TokenType, Mask(device.Token));
            return PushSendOutcome.TransientFailure;
        }
    }

    private PushSendOutcome LogUnsupported(IPushDeviceReadModel device)
    {
        logger.LogDebug("Push token type {Type} is not supported yet; skip push to permAuthKeyId={PermAuthKeyId}",
            device.TokenType, device.PermAuthKeyId);
        return PushSendOutcome.Delivered;
    }

    private static string Mask(string token) =>
        string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)] + "***";
}
