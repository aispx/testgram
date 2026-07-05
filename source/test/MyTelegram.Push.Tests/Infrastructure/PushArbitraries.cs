using FsCheck;
using MyTelegram.Core;

namespace MyTelegram.Push.Tests.Infrastructure;

/// <summary>
/// FsCheck <see cref="Arbitrary{T}"/> registration surface for the push-updates property tests.
/// Reference it from a test with
/// <c>[Properties(Arbitrary = new[] { typeof(PushArbitraries) })]</c> (class level) or
/// <c>[Property(Arbitrary = new[] { typeof(PushArbitraries) })]</c> (method level) and FsCheck will
/// resolve generators for the custom types below automatically.
/// <para>
/// Note: a raw <c>byte[]</c> arbitrary is intentionally NOT registered (to avoid overriding FsCheck's
/// default byte-array generation); use <see cref="PushGen.Secret256"/> via <c>Arb.From(...)</c> when a
/// 256-byte secret is needed directly.
/// </para>
/// </summary>
public static class PushArbitraries
{
    public static Arbitrary<DeviceRegistration> DeviceRegistration() => Arb.From(PushGen.ValidRegistration);

    public static Arbitrary<RegistrationCase> RegistrationCase() => Arb.From(PushGen.RegistrationCase);

    public static Arbitrary<WebPushTokenCase> WebPushTokenCase() => Arb.From(PushGen.WebPushTokenCase);

    public static Arbitrary<PushData> PushData() => Arb.From(PushGen.PushData);

    public static Arbitrary<PushNotificationCustomData> CustomData() => Arb.From(PushGen.CustomData);

    public static Arbitrary<MessageCase> MessageCase() => Arb.From(PushGen.MessageCase);

    public static Arbitrary<DeviceSet> DeviceSet() => Arb.From(PushGen.DeviceSet);

    public static Arbitrary<ProviderConfigCase> ProviderConfig() => Arb.From(PushGen.ProviderConfig);
}
