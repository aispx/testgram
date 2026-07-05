// Feature: push-updates, EXAMPLE 2.4: интеграция ReadModel↔Шифратор —
// устройство с непустым 256-байтным Secret шифрует payload (MTProto v2),
// устройство без Secret (null) отдаёт незашифрованный base64url JSON.
//
// This is the unit-level example for the ReadModel↔Encryptor integration described in the design
// (Testing Strategy, EXAMPLE 2.4) backing Requirement 5.4. It builds two PushDeviceReadModel test
// doubles — one WITH a 256-byte Secret and one WITHOUT (Secret == null) — and reads device.Secret
// exactly as the delivery loop in PushNotificationEventHandler does, feeding it into the production
// PushPayloadEncryptor.EncryptForDevice with a real MtpHelper / AuthKeyIdHelper.
//
// Asserts:
//   * With a secret  -> wire is the MTProto v2 encrypted form (auth_key_id + msg_key + AES-IGE),
//                       decryptable by the independent task-1 reference oracle back to BuildJson(data),
//                       and is NOT equal to the plaintext base64url fallback.
//   * Without a secret -> wire equals base64url of the plaintext JSON and decodes back to BuildJson(data).
//
// Validates: Requirements 5.4

using System.Text;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel;
using MyTelegram.Services.Services;
using Shouldly;
using Xunit;

namespace MyTelegram.Push.Tests;

public class ReadModelEncryptorIntegrationTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    /// <summary>A deterministic, representative push payload (as the builders would produce).</summary>
    private static PushData SamplePushData() => new(
        LocKey: PushNotificationTypes.MessageText,
        LocArgs: new[] { "Alice", "hello there" },
        UserId: 42,
        Custom: new PushNotificationCustomData
        {
            MsgId = 12345,
            FromId = 7,
            Silent = false,
            Mention = false
        },
        Sound: "default");

    /// <summary>A deterministic 256-byte push secret (auth key) for MTProto v2 encryption.</summary>
    private static byte[] Secret256()
    {
        var secret = new byte[MtProtoV2ReferenceCrypto.SecretLength];
        for (var i = 0; i < secret.Length; i++)
        {
            secret[i] = (byte)((i * 37 + 11) & 0xFF);
        }

        return secret;
    }

    // EXAMPLE 2.4: device WITH a 256-byte Secret -> encrypted MTProto v2 wire format.
    // Validates: Requirements 5.4
    [Fact]
    public void Device_with_secret_encrypts_payload_as_mtproto_v2()
    {
        var data = SamplePushData();
        var expectedJson = PushPayloadEncryptor.BuildJson(data);
        var plaintextBase64Url = PushPayloadEncryptor.Base64UrlEncode(Encoding.UTF8.GetBytes(expectedJson));

        // A registered device that carries a 256-byte push secret.
        IPushDeviceReadModel device = new FakePushDeviceReadModel
        {
            Id = "device-with-secret",
            UserId = 42,
            PermAuthKeyId = 9001,
            TokenType = 2,
            Token = "fcm-token-abc",
            Secret = Secret256()
        };

        // Read device.Secret exactly as the delivery handler does and encrypt.
        var wire = PushPayloadEncryptor.EncryptForDevice(device.Secret, data, MtpHelper, AuthKeyIdHelper);

        // It must NOT be the plaintext base64url fallback.
        wire.ShouldNotBe(plaintextBase64Url);

        // It must be the MTProto v2 encrypted wire format: decryptable by the independent reference
        // oracle back to the exact JSON, with a valid msg_key and auth_key_id.
        var result = MtProtoV2ReferenceCrypto.DecryptBase64Url(device.Secret!, wire);
        result.Json.ShouldBe(expectedJson);
        result.MsgKeyValid.ShouldBeTrue();
        result.AuthKeyIdValid.ShouldBeTrue();
    }

    // EXAMPLE 2.4: device WITHOUT a Secret (null) -> plaintext base64url fallback.
    // Validates: Requirements 5.4
    [Fact]
    public void Device_without_secret_returns_plaintext_base64url()
    {
        var data = SamplePushData();
        var expectedJson = PushPayloadEncryptor.BuildJson(data);
        var jsonBytes = Encoding.UTF8.GetBytes(expectedJson);
        var expectedWire = PushPayloadEncryptor.Base64UrlEncode(jsonBytes);

        // A registered device that carries NO push secret.
        IPushDeviceReadModel device = new FakePushDeviceReadModel
        {
            Id = "device-without-secret",
            UserId = 42,
            PermAuthKeyId = 9002,
            TokenType = 2,
            Token = "fcm-token-xyz",
            Secret = null
        };

        device.Secret.ShouldBeNull();

        // Read device.Secret exactly as the delivery handler does and encrypt.
        var wire = PushPayloadEncryptor.EncryptForDevice(device.Secret, data, MtpHelper, AuthKeyIdHelper);

        // The output equals base64url of the unencrypted JSON payload ...
        wire.ShouldBe(expectedWire);

        // ... and base64url-decoding it (independent reference codec) restores BuildJson(data).
        var decoded = Base64UrlReference.Decode(wire);
        decoded.ShouldBe(jsonBytes);
        Encoding.UTF8.GetString(decoded).ShouldBe(expectedJson);
    }
}
