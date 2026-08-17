// Feature: push-updates, Property 17: Payload encryption is reversible (round-trip)
//
// For any JSON payload (built by PushPayloadEncryptor.BuildJson from a generated PushData) and any
// 256-byte Secret, encrypting via the production PushPayloadEncryptor.EncryptForDevice and then
// decrypting with the independent MTProto v2 reference decryptor (msg_key from
// SHA256(secret[96..128] + payload), AES-IGE v2 key/iv derivation with x = 8 — exactly as official
// clients reconstruct the keys) restores the original JSON payload losslessly.
//
// The production encryptor is driven with a real MtpHelper/AuthKeyIdHelper and a 256-byte secret
// (reusing the task-1 generators). The wire output is base64url-decoded and decrypted by the task-1
// reference oracle; the recovered JSON must byte-for-byte equal BuildJson(data), and the recomputed
// msg_key / auth_key_id must validate (proving the wire format is what a client would accept).
//
// Validates: Requirements 5.5

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Services.Services;

namespace MyTelegram.Push.Tests;

public class Property17_EncryptionRoundTripTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 17: Payload encryption is reversible (round-trip)
    // Validates: Requirements 5.5
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public Property Encrypting_then_decrypting_restores_the_original_json(PushData data)
    {
        return Prop.ForAll(Arb.From(PushGen.Secret256), secret =>
        {
            var expectedJson = PushPayloadEncryptor.BuildJson(data);

            // Production encryption with a 256-byte secret -> base64url MTProto v2 wire format.
            var wire = PushPayloadEncryptor.EncryptForDevice(secret, data, MtpHelper, AuthKeyIdHelper);

            // Independent reference decryptor (the trustworthy client-side oracle).
            var result = MtProtoV2ReferenceCrypto.DecryptBase64Url(secret, wire);

            return result.Json == expectedJson
                   && result.MsgKeyValid
                   && result.AuthKeyIdValid;
        });
    }
}
