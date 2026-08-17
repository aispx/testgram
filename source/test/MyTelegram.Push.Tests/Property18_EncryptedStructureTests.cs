// Feature: push-updates, Property 18: The encrypted payload structure matches MTProto v2
//
// For any JSON payload (built by PushPayloadEncryptor.BuildJson from a generated PushData) and any
// 256-byte Secret, the production PushPayloadEncryptor.EncryptForDevice output, once base64url-decoded
// with the reference codec, has the MTProto v2 wire structure:
//
//     [auth_key_id : 8][msg_key : 16][aes_ige( [int32_le len][json][padding] )]
//
// We assert:
//   * total length >= 24 and (length - 24) is a multiple of 16 (the encrypted part is block-aligned),
//   * the first 8 bytes equal auth_key_id = AuthKeyIdHelper.GetAuthKeyId(secret) (little-endian int64),
//   * the next 16 bytes are the msg_key, and
//   * decrypting the aes_ige part with the independent task-1 reference decryptor yields an inner
//     buffer whose first 4 bytes (int32 little-endian) equal the JSON byte length.
//
// The encryptor is driven with a real MtpHelper/AuthKeyIdHelper (the MtpHelper IV path fixed in
// task 14.1). Generators and the reference decryptor are reused from the task-1 infrastructure.
//
// Validates: Requirements 5.1, 5.2

using System.Buffers.Binary;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Services.Services;

namespace MyTelegram.Push.Tests;

public class Property18_EncryptedStructureTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 18: The encrypted payload structure matches MTProto v2
    // Validates: Requirements 5.1, 5.2
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public Property Encrypted_payload_has_mtproto_v2_structure(PushData data)
    {
        return Prop.ForAll(Arb.From(PushGen.Secret256), secret =>
        {
            var json = PushPayloadEncryptor.BuildJson(data);
            var jsonByteLength = Encoding.UTF8.GetByteCount(json);

            // Production encryption with a 256-byte secret -> base64url MTProto v2 wire format.
            var wire = PushPayloadEncryptor.EncryptForDevice(secret, data, MtpHelper, AuthKeyIdHelper);
            var raw = Base64UrlReference.Decode(wire);

            // [auth_key_id : 8][msg_key : 16][aes_ige(...)] with the encrypted part block-aligned.
            var hasHeader = raw.Length >= 24;
            var encryptedAligned = hasHeader && (raw.Length - 24) % 16 == 0;

            // First 8 bytes == auth_key_id (little-endian int64).
            var expectedAuthKeyId = AuthKeyIdHelper.GetAuthKeyId(secret);
            var authKeyIdOnWire = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(0, 8));
            var authKeyIdMatches = authKeyIdOnWire == expectedAuthKeyId;

            // Next 16 bytes == msg_key; reference decryptor verifies it and exposes the inner buffer's
            // declared length (the first 4 bytes, int32 little-endian).
            var result = MtProtoV2ReferenceCrypto.DecryptBase64Url(secret, wire);
            var msgKeyPresent = result.MsgKey.Length == 16;
            var innerLengthMatches = result.DeclaredLength == jsonByteLength;

            return (hasHeader
                    && encryptedAligned
                    && authKeyIdMatches
                    && msgKeyPresent
                    && result.MsgKeyValid
                    && result.AuthKeyIdValid
                    && innerLengthMatches)
                .Label($"len={raw.Length}, aligned={encryptedAligned}, authKeyId={authKeyIdMatches}, " +
                       $"declaredLen={result.DeclaredLength}, jsonLen={jsonByteLength}");
        });
    }
}
