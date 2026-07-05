using MyTelegram.Messenger;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// Shared wrapper/DTO types produced by the FsCheck generators. Keeping them here (rather than
/// generating raw schema objects everywhere) gives later property tests strongly-typed,
/// self-describing inputs together with the classification needed to assert the expected outcome.
/// </summary>
public static class PushTokenTypes
{
    /// <summary>Token types accepted by <c>account.registerDevice</c> (Requirement 1.3).</summary>
    public static readonly IReadOnlyList<int> Supported = new[] { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13 };

    public static bool IsSupported(int tokenType) => Supported.Contains(tokenType);
}

/// <summary>A device-registration request together with the authenticated request context.</summary>
public sealed record DeviceRegistration(
    long UserId,
    long PermAuthKeyId,
    int TokenType,
    string Token,
    byte[] Secret,
    bool NoMuted,
    bool AppSandbox,
    IReadOnlyList<long> OtherUids)
{
    public override string ToString() =>
        $"DeviceRegistration(User={UserId}, AuthKey={PermAuthKeyId}, Type={TokenType}, " +
        $"Token='{Token}', SecretLen={Secret.Length}, OtherUids=[{string.Join(",", OtherUids)}])";
}

/// <summary>How a generated registration request is expected to be classified by the validator.</summary>
public enum RegistrationValidity
{
    Valid,
    TokenEmpty,
    TokenTypeInvalid,
    WebPushTokenInvalid,
    WebPushAuthInvalid,
    WebPushKeyInvalid
}

/// <summary>A registration request paired with the validity outcome it should produce.</summary>
public sealed record RegistrationCase(DeviceRegistration Registration, RegistrationValidity ExpectedValidity)
{
    public override string ToString() => $"{ExpectedValidity}: {Registration}";
}

/// <summary>Classification of a generated Web-push (token_type 10) JSON token.</summary>
public enum WebPushTokenKind
{
    Valid,
    MissingEndpoint,
    MissingAuth,
    InvalidAuth,
    MissingKey,
    InvalidKey
}

/// <summary>A Web-push JSON token string and the validation outcome it should produce.</summary>
public sealed record WebPushTokenCase(string Json, WebPushTokenKind Kind)
{
    public override string ToString() => $"{Kind}: {Json}";
}

/// <summary>Coarse classification of a generated <see cref="MessageItem"/> fixture.</summary>
public enum MessageKind
{
    Text,
    Media,
    Reaction,
    Call
}

/// <summary>A message fixture with its kind and the resolved peer, for payload-builder tests.</summary>
public sealed record MessageCase(MessageItem Item, MessageKind Kind, PeerType PeerType)
{
    public override string ToString() => $"{Kind} ({PeerType}) msgId={Item.MessageId}";
}

/// <summary>A set of devices for one delivery, deliberately containing token/OtherUids overlaps.</summary>
public sealed record DeviceSet(IReadOnlyList<FakePushDeviceReadModel> Devices, long RecipientUserId)
{
    public override string ToString() =>
        $"DeviceSet(recipient={RecipientUserId}, devices={Devices.Count}, " +
        $"uniqueTokens={Devices.Select(d => d.Token).Distinct().Count()})";
}

/// <summary>A provider <see cref="PushConfig"/> together with whether credentials make it enabled.</summary>
public sealed record ProviderConfigCase(PushConfig Config)
{
    public override string ToString() =>
        $"PushConfig(master={Config.Enabled}, fcm={Config.Fcm.Enabled}, apns={Config.Apns.Enabled}, web={Config.WebPush.Enabled})";
}
