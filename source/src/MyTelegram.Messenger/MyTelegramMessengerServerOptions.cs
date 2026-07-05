using System.ComponentModel.DataAnnotations;

namespace MyTelegram.Messenger;
#nullable disable
public class MyTelegramMessengerServerOptions
{
    public string FileServerGrpcServiceUrl { get; set; }


    [RegularExpression("^([\\d]{3,6})|(\\s*)$")]
    public string FixedVerifyCode { get; set; }

    [Range(3, 6)]
    public int VerificationCodeLength { get; set; } = 5;

    [Range(60, int.MaxValue)]
    public int VerificationCodeExpirationSeconds { get; set; } = 300;
    public string JoinChatDomain { get; set; }

    public int ChannelGetDifferenceIntervalSeconds { get; set; }

    public bool UseInMemoryFilters { get; set; }
    public int EditTimeLimit { get; set; }
    public List<WebRtcConnection> WebRtcConnections { get; set; }
    public int ThisDcId { get; set; }
    public List<DcOption> DcOptions { get; set; }
    public bool AutoCreateSuperGroup { get; set; }
    public bool EnableFutureAuthToken { get; set; }
    public bool SetPremiumToTrueAfterUserCreated { get; set; }
    public bool SendWelcomeMessageAfterUserSignIn { get; set; }
    public bool SetupPasswordRequired { get; set; }
    public bool EnableEmailLogin { get; set; }

    [RegularExpression("^([\\d]{6})|(\\s*)$")]
    public string FixedEmailVerificationCode { get; set; }

    public string? PasskeyRpId { get; set; }
    public string? PasskeyRpName { get; set; }
    public int PasskeysAccountPasskeysMax { get; set; } = 20;

    //public long? SupportUserId { get; set; }
    // https://github.com/dotnet/runtime/issues/36510
    [RegularExpression("^([\\d]{1,19})|(\\s*)$")]
    public string SupportUserId { get; set; }
    public int MaxInMemoryContactCount { get; set; }
    public bool CheckPhoneNumberFormat { get; set; }
    public bool EnableSearchNonContacts { get; set; }
    public int RpcResultExpirationMinutes { get; set; }
    public string RtmpStreamUrl { get; set; } = "rtmp://testgram.xie.su:1935/live";
    public string RtmpHlsUrl { get; set; } = "http://rtmp-server:8888/live";
    public EncryptionConfig EncryptionConfig { get; set; }
    public StripeConfig Stripe { get; set; } = new();
    public PushConfig Push { get; set; } = new();
}

/// <summary>
/// Push-notification (FCM/APNS/APNS-VoIP/Web-Push) delivery configuration.
/// Mirrors https://corefork.telegram.org/api/push-updates . Disabled by default; set
/// <c>Enabled=true</c> and fill in provider credentials to activate delivery.
/// </summary>
public class PushConfig
{
    /// <summary>Master switch. When false, no push payloads are dispatched to providers.</summary>
    public bool Enabled { get; set; } = false;

    public FcmConfig Fcm { get; set; } = new();
    public ApnsConfig Apns { get; set; } = new();
    public WebPushConfig WebPush { get; set; } = new();

    /// <summary>
    /// Firebase Cloud Messaging (token_type = 2). Uses the HTTP v1 API with a service-account JSON.
    /// </summary>
    public class FcmConfig
    {
        /// <summary>Path to the Firebase service-account JSON file, or the JSON contents inline.</summary>
        public string ServiceAccountJson { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(ServiceAccountJson);
    }

    /// <summary>
    /// Apple Push Notification service (token_type = 1 APNS, 9 APNS VoIP).
    /// </summary>
    public class ApnsConfig
    {
        /// <summary>Contents of the .p8 APNs Auth Key (Apple Developer "Keys").</summary>
        public string AuthKeyP8 { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string TeamId { get; set; } = string.Empty;
        public string BundleId { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(AuthKeyP8)
                               && !string.IsNullOrWhiteSpace(KeyId)
                               && !string.IsNullOrWhiteSpace(TeamId);
    }

    /// <summary>
    /// Web Push (token_type = 10). Token is a JSON object with endpoint/keys.p256dh/keys.auth.
    /// </summary>
    public class WebPushConfig
    {
        /// <summary>VAPID private key (P-256) as base64url, used to sign push messages.</summary>
        public string VapidPrivateKey { get; set; } = string.Empty;
        /// <summary>VAPID public key (P-256) as base64url.</summary>
        public string VapidPublicKey { get; set; } = string.Empty;
        /// <summary>mailto: or https:// contact for VAPID JWT "sub".</summary>
        public string VapidSubject { get; set; } = string.Empty;
        public int PushTimeoutSec { get; set; } = 30;
        public bool Enabled => !string.IsNullOrWhiteSpace(VapidPrivateKey)
                               && !string.IsNullOrWhiteSpace(VapidPublicKey);
    }
}

public class EncryptionConfig
{
    public bool Enabled { get; set; }
    public string PhoneKey { get; set; }
    public List<KeyConfig> IndexKeys { get; set; }
    public List<KeyConfig> MessageKeys { get; set; }
}

public class KeyConfig
{
    public int Id { get; set; }
    public string Key { get; set; }
}


public class StripeConfig
{
    public string PublishableKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}
