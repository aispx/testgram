// Feature: push-updates, Property 20: Fallback without a secret — reversible unencrypted base64url
// (no-secret fallback is a reversible, unencrypted base64url)
//
// For any PushData, when the device has no push secret (Secret == null OR Secret == empty array),
// the production PushPayloadEncryptor.EncryptForDevice returns the base64url of the UNENCRYPTED JSON
// payload — i.e. it equals PushPayloadEncryptor.Base64UrlEncode(UTF8 bytes of BuildJson(data)) — and
// base64url-decoding that wire string (with the independent test Infrastructure reference codec)
// restores exactly the bytes of BuildJson(data) (Requirement 5.4).
//
// This drives the REAL production encryptor (with a real MtpHelper/AuthKeyIdHelper, which are never
// exercised on the no-secret path) over the task-1 PushData generator, asserting the fallback wire
// contract two ways: the output equals the production base64url encoding of the plaintext JSON, and
// an independent base64url decoder recovers the original JSON byte-for-byte. Both the null and the
// empty-array secret cases are checked.
//
// Validates: Requirements 5.4

using System.Text;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Services.Services;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property20_NoSecretFallbackTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 20: Fallback without a secret — reversible unencrypted base64url
    // Validates: Requirements 5.4
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void NoSecret_fallback_is_reversible_unencrypted_base64url(PushData data)
    {
        var json = PushPayloadEncryptor.BuildJson(data);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var expectedWire = PushPayloadEncryptor.Base64UrlEncode(jsonBytes);

        // Both "no secret" representations must take the unencrypted fallback path.
        foreach (var secret in new byte[]?[] { null, Array.Empty<byte>() })
        {
            var wire = PushPayloadEncryptor.EncryptForDevice(secret, data, MtpHelper, AuthKeyIdHelper);

            // (1) The output equals base64url of the unencrypted JSON payload.
            wire.ShouldBe(expectedWire);

            // (2) base64url-decoding the wire (independent reference codec) restores exactly
            //     the bytes of BuildJson(data).
            var decoded = Base64UrlReference.Decode(wire);
            decoded.ShouldBe(jsonBytes);
            Encoding.UTF8.GetString(decoded).ShouldBe(json);
        }
    }
}
