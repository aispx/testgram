using MyTelegram.ReadModel;

namespace MyTelegram.Messenger.QueryServer.Services;

/// <summary>
/// Outcome of a push delivery attempt, used to propagate stale-token signals from the providers
/// up to the delivery service so it can clean up invalidated tokens.
/// <para>See https://corefork.telegram.org/api/push-updates .</para>
/// </summary>
public enum PushSendOutcome
{
    /// <summary>The provider accepted the notification for delivery.</summary>
    Delivered,

    /// <summary>A transient error occurred (non-2xx, timeout, network); the token is still valid.</summary>
    TransientFailure,

    /// <summary>The provider reported the token as no longer valid (APNs 410, FCM 404 UNREGISTERED).</summary>
    TokenInvalidated
}

/// <summary>
/// Delivers an MTProto-encrypted (base64url) push payload to a specific registered device,
/// routing by <see cref="IPushDeviceReadModel.TokenType"/> (2=FCM, 1=APNS, 9=APNS VoIP, 10=Web Push).
/// <para>See https://corefork.telegram.org/api/push-updates .</para>
/// </summary>
public interface IPushDispatcher
{
    /// <summary>
    /// Sends <paramref name="base64Payload"/> (the value of the provider "p"/data field) to the
    /// given device and returns the delivery <see cref="PushSendOutcome"/> so the caller can react
    /// to stale tokens. Does not throw on transient failures.
    /// </summary>
    Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload);
}

/// <summary>Token-type constants from <c>account.registerDevice</c>.</summary>
public static class PushTokenType
{
    public const int Apns = 1;        // APNS (device token for apple push)
    public const int Fcm = 2;         // FCM (firebase token for google firebase)
    public const int Mpns = 3;        // MPNS (channel URI for microsoft push)
    public const int Ubuntu = 5;      // Ubuntu phone
    public const int BlackBerry = 6;  // BlackBerry
    public const int InternalPush = 7; // Android native MTProto push-session registration
    public const int Wns = 8;         // WNS (windows push)
    public const int ApnsVoip = 9;    // APNS VoIP
    public const int WebPush = 10;    // Web push
    public const int MpnsVoip = 11;   // MPNS VoIP
    public const int Tizen = 12;      // Tizen
    public const int Huawei = 13;     // Huawei push
}
