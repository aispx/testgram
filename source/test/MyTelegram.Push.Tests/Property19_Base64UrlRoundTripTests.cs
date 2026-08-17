// Feature: push-updates, Property 19: base64url encoding is reversible and unpadded
// (base64url encoding is reversible and unpadded)
//
// For any byte array, the production PushPayloadEncryptor.Base64UrlEncode produces a string that
// contains none of the standard-base64 characters '+', '/', '=' (i.e. it uses the URL-safe alphabet
// and is unpadded), and base64url-decoding that string returns the original bytes unchanged
// (Requirement 5.3).
//
// This drives the REAL production encoder over arbitrary byte arrays (including the empty array and
// lengths that are not multiples of 3, which is exactly where standard base64 would emit '=' padding)
// and verifies the wire contract two ways: the encoded text is checked to be free of '+', '/', '=',
// and an independent base64url reference decoder (test Infrastructure, task 1) restores the original
// bytes byte-for-byte.
//
// Validates: Requirements 5.3

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property19_Base64UrlRoundTripTests
{
    /// <summary>
    /// Arbitrary byte arrays of length 0..64. The 0..2 short lengths and any length not a multiple of
    /// 3 force the cases where standard base64 emits '=' padding, so the no-padding contract is
    /// exercised. Built with the task-1 <see cref="GenHelpers.ArrayOfLength{T}"/> helper.
    /// </summary>
    private static Gen<byte[]> ByteArray =>
        from length in Gen.Choose(0, 64)
        from bytes in GenHelpers.ArrayOfLength(length, Gen.Choose(0, 255).Select(i => (byte)i))
        select bytes;

    // Property 19: base64url encoding is reversible and unpadded
    // Validates: Requirements 5.3
    [Property(MaxTest = 100)]
    public Property Base64Url_encode_is_unpadded_url_safe_and_round_trips()
    {
        return Prop.ForAll(Arb.From(ByteArray), bytes =>
        {
            var encoded = PushPayloadEncryptor.Base64UrlEncode(bytes);

            // No standard-base64 characters: URL-safe alphabet ('-'/'_') and no '=' padding.
            encoded.ShouldNotContain("+");
            encoded.ShouldNotContain("/");
            encoded.ShouldNotContain("=");

            // Decoding with the independent reference codec restores the original bytes exactly.
            var decoded = Base64UrlReference.Decode(encoded);
            decoded.ShouldBe(bytes);

            return true;
        });
    }
}
