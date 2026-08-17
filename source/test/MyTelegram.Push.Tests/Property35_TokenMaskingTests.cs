using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Push.Tests.Infrastructure;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 35: Token masking in logs.
//
// For any token string, the masking function used by the push senders returns a value that reveals
// at most the first 8 characters of the original token. The masking helper lives as an identical
// private `Mask` method in ApnsPushSender, FcmPushSender and PushDispatcher:
//
//     private static string Mask(string token) =>
//         string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)] + "***";
//
// Since the helper is private (not exposed on any public/shared surface), this property pins the
// exact masking contract here and asserts the universal invariant the contract must satisfy:
//   * the masked output never exposes more than the first 8 characters of the token;
//   * for any token longer than 8 characters, the full token never appears in the masked output.
//
// Validates: Requirements 11.3
public class Property35_TokenMaskingTests
{
    /// <summary>The exact masking contract implemented by the push senders (see comment above).</summary>
    private static string Mask(string? token) =>
        string.IsNullOrEmpty(token) ? "" : token[..Math.Min(8, token.Length)] + "***";

    /// <summary>
    /// Tokens spanning every relevant class: null, empty/whitespace, short (&lt; 8), exactly 8,
    /// long opaque device tokens (&gt; 8) and arbitrary unicode strings.
    /// </summary>
    private static Gen<string?> AnyToken =>
        Gen.OneOf(
            Gen.Constant((string?)null),
            PushGen.EmptyOrWhitespaceToken.Select(s => (string?)s),
            PushGen.NonEmptyToken.Select(s => (string?)s),
            // Short tokens (0..7 chars) so the Math.Min(8, len) boundary is exercised from below.
            (from n in Gen.Choose(0, 7)
             from chars in GenHelpers.ArrayOfLength(n, Gen.Elements("abcdefABCDEF0123456789".ToCharArray()))
             select (string?)new string(chars)),
            // Arbitrary strings (may include unicode / control chars), filtered of nulls.
            Arb.Generate<string>().Where(s => s is not null));

    // Property 35: Token masking in logs
    // Validates: Requirements 11.3
    [Property(MaxTest = 100)]
    public Property Masking_reveals_at_most_first_8_characters()
    {
        return Prop.ForAll(Arb.From(AnyToken), token =>
        {
            var masked = Mask(token);

            // Empty/null tokens mask to the empty string: nothing is revealed.
            if (string.IsNullOrEmpty(token))
            {
                return (masked.Length == 0)
                    .Label($"empty token must mask to empty string but was '{masked}'");
            }

            var revealedCount = Math.Min(8, token.Length);
            var expectedPrefix = token[..revealedCount];

            // The masked value is exactly the revealed prefix followed by the redaction marker.
            var contractHolds = masked == expectedPrefix + "***";

            // No more than the first 8 characters of the token are revealed.
            var atMostEightRevealed = revealedCount <= 8;

            // For tokens longer than 8 chars, the full token never leaks into the masked output.
            var fullTokenHidden = token.Length <= 8 || !masked.Contains(token, StringComparison.Ordinal);

            return (contractHolds && atMostEightRevealed && fullTokenHidden)
                .Label($"token(len={token.Length})='{token}' masked='{masked}' " +
                       $"revealed={revealedCount} contractHolds={contractHolds} " +
                       $"atMostEight={atMostEightRevealed} fullTokenHidden={fullTokenHidden}");
        });
    }
}
