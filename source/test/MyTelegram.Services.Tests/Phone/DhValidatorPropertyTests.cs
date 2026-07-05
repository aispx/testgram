using System.Numerics;
using System.Security.Cryptography;
using CsCheck;
using MyTelegram.Core;
using MyTelegram.Services.Phone;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based tests (CsCheck) for the 1:1 call Diffie-Hellman validator
/// (<see cref="PhoneCallDhValidator"/>).
///
/// Covers design Property 5 (DH range safety) and Property 4 (DH hash binding).
///
/// The server never learns the shared key; these properties assert only the structural/range
/// checks the server performs on the values it relays during <c>acceptCall</c> (g_b) and
/// <c>confirmCall</c> (g_a + g_a_hash).
/// </summary>
public class DhValidatorPropertyTests
{
    /// <summary>The shared 2048-bit DH prime <c>p</c>.</summary>
    private static readonly BigInteger Prime = AuthConsts.DhPrime;

    /// <summary>The safety bound <c>2^{2048-64}</c>. A value is valid iff it lies in <c>[SafetyRange, p - SafetyRange]</c>.</summary>
    private static readonly BigInteger SafetyRange = BigInteger.One << (2048 - 64);

    /// <summary>Upper safety bound <c>p - 2^{2048-64}</c>.</summary>
    private static readonly BigInteger UpperSafetyBound = Prime - SafetyRange;

    // ---- generators ------------------------------------------------------------------------------

    /// <summary>
    /// Uniform-ish non-negative <see cref="BigInteger"/> in <c>[0, bound)</c>. We draw 264 random bytes
    /// (2112 bits, comfortably wider than the 2048-bit prime) and reduce modulo <paramref name="bound"/>;
    /// the modulo bias is negligible for a generator this much wider than the range.
    /// </summary>
    private static Gen<BigInteger> BigIntegerBelow(BigInteger bound) =>
        Gen.Byte.Array[264].Select(bytes =>
        {
            var raw = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
            return raw % bound;
        });

    /// <summary>Values guaranteed to be in the valid range <c>[SafetyRange, p - SafetyRange]</c>.</summary>
    private static Gen<BigInteger> ValidValue =>
        BigIntegerBelow(UpperSafetyBound - SafetyRange + BigInteger.One).Select(x => SafetyRange + x);

    /// <summary>Values below the lower safety bound: <c>[0, SafetyRange - 1]</c> (too small, must be rejected).</summary>
    private static Gen<BigInteger> TooSmallValue =>
        BigIntegerBelow(SafetyRange);

    /// <summary>Values above the upper safety bound but still below the prime: <c>(p - SafetyRange, p - 1]</c>.</summary>
    private static Gen<BigInteger> TooLargeValue =>
        BigIntegerBelow(SafetyRange - BigInteger.One).Select(x => UpperSafetyBound + BigInteger.One + x);

    /// <summary>Values at or above the prime: <c>[p, p + SafetyRange)</c> (out of the field entirely).</summary>
    private static Gen<BigInteger> AtOrAbovePrime =>
        BigIntegerBelow(SafetyRange).Select(x => Prime + x);

    /// <summary>Big-endian unsigned byte encoding, as clients transmit <c>g_a</c>/<c>g_b</c>.</summary>
    private static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    // ---- Property 5: DH range safety -------------------------------------------------------------

    /// <summary>
    /// Property 5 (accept side): any value in <c>[SafetyRange, p - SafetyRange]</c> satisfies both
    /// <c>1 &lt; g &lt; p - 1</c> and the safety bound, so it is accepted.
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void ValuesInsideSafetyRange_AreAccepted()
    {
        ValidValue.Sample(
            g => PhoneCallDhValidator.IsValidDhValue(ToBytes(g)),
            iter: 1000);
    }

    /// <summary>
    /// Property 5 (reject side): values below the safety bound are rejected. Such values would violate
    /// <c>g &gt;= 2^{2048-64}</c> even when they satisfy the plain <c>g &gt; 1</c> bound.
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void ValuesBelowSafetyRange_AreRejected()
    {
        TooSmallValue.Sample(
            g => !PhoneCallDhValidator.IsValidDhValue(ToBytes(g)),
            iter: 1000);
    }

    /// <summary>
    /// Property 5 (reject side): values above <c>p - SafetyRange</c> (but below <c>p</c>) are rejected.
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void ValuesAboveSafetyRange_AreRejected()
    {
        TooLargeValue.Sample(
            g => !PhoneCallDhValidator.IsValidDhValue(ToBytes(g)),
            iter: 1000);
    }

    /// <summary>
    /// Property 5 (reject side): values at or above the prime are rejected (outside the field).
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void ValuesAtOrAbovePrime_AreRejected()
    {
        AtOrAbovePrime.Sample(
            g => !PhoneCallDhValidator.IsValidDhValue(ToBytes(g)),
            iter: 1000);
    }

    /// <summary>
    /// Property 5 (agreement): across the full spread of candidate values the validator agrees exactly
    /// with the mathematical definition <c>SafetyRange &lt;= g &lt;= p - SafetyRange</c>.
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void ValidatorAgreesWithSafetyBoundDefinition()
    {
        var anyValue = Gen.OneOf(ValidValue, TooSmallValue, TooLargeValue, AtOrAbovePrime);

        anyValue.Sample(
            g =>
            {
                var expected = g >= SafetyRange && g <= UpperSafetyBound;
                return PhoneCallDhValidator.IsValidDhValue(ToBytes(g)) == expected;
            },
            iter: 1000);
    }

    /// <summary>
    /// Property 5 boundary examples: the exact edges of the accepted interval and just outside it.
    ///
    /// **Validates: Requirements 4.7, 5.4**
    /// </summary>
    [Fact]
    public void SafetyBoundEdges_AreClassifiedCorrectly()
    {
        // Just inside the interval -> accepted.
        PhoneCallDhValidator.IsValidDhValue(ToBytes(SafetyRange)).ShouldBeTrue();
        PhoneCallDhValidator.IsValidDhValue(ToBytes(UpperSafetyBound)).ShouldBeTrue();

        // Just outside the interval -> rejected.
        PhoneCallDhValidator.IsValidDhValue(ToBytes(SafetyRange - BigInteger.One)).ShouldBeFalse();
        PhoneCallDhValidator.IsValidDhValue(ToBytes(UpperSafetyBound + BigInteger.One)).ShouldBeFalse();

        // Degenerate plain-bound violations -> rejected.
        PhoneCallDhValidator.IsValidDhValue(ToBytes(BigInteger.Zero)).ShouldBeFalse();
        PhoneCallDhValidator.IsValidDhValue(ToBytes(BigInteger.One)).ShouldBeFalse();
        PhoneCallDhValidator.IsValidDhValue(ToBytes(Prime - BigInteger.One)).ShouldBeFalse();
        PhoneCallDhValidator.IsValidDhValue(ToBytes(Prime)).ShouldBeFalse();

        // Empty / null -> rejected.
        PhoneCallDhValidator.IsValidDhValue(null).ShouldBeFalse();
        PhoneCallDhValidator.IsValidDhValue(Array.Empty<byte>()).ShouldBeFalse();
    }

    // ---- Property 4: DH hash binding -------------------------------------------------------------

    /// <summary>
    /// Property 4 (binding holds): for any <c>g_a</c>, the hash recorded at <c>requestCall</c> is
    /// <c>SHA256(g_a)</c>, so at <c>confirmCall</c> the validator accepts exactly that pairing. This is
    /// the precondition for a call reaching <c>confirmed</c>.
    ///
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public void GaMatchingItsSha256Hash_IsAccepted()
    {
        Gen.Byte.Array[1, 256].Sample(
            ga => PhoneCallDhValidator.IsGaHashValid(ga, SHA256.HashData(ga)),
            iter: 1000);
    }

    /// <summary>
    /// Property 4 (mismatch rejected): if the presented <c>g_a</c> does not hash to the recorded
    /// <c>g_a_hash</c>, the validator rejects it, so the call cannot reach <c>confirmed</c>. We flip a
    /// single random bit of the true hash to guarantee a genuine mismatch.
    ///
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public void GaNotMatchingHash_IsRejected()
    {
        var mismatchCase =
            from ga in Gen.Byte.Array[1, 256]
            from byteIndex in Gen.Int[0, 31]
            from bit in Gen.Int[0, 7]
            select (ga, byteIndex, bit);

        mismatchCase.Sample(
            c =>
            {
                var tampered = SHA256.HashData(c.ga);
                tampered[c.byteIndex] ^= (byte)(1 << c.bit);
                return !PhoneCallDhValidator.IsGaHashValid(c.ga, tampered);
            },
            iter: 1000);
    }

    /// <summary>
    /// Property 4 (independence): swapping the <c>g_a</c> for a different value while keeping the old
    /// hash is rejected (SHA-256 collision resistance in practice). Distinct inputs almost never share a
    /// hash, so the validator must reject the swapped pairing.
    ///
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public void HashBoundToOriginalGa_RejectsSwappedGa()
    {
        var pairGen =
            from a in Gen.Byte.Array[1, 256]
            from b in Gen.Byte.Array[1, 256]
            where !a.AsSpan().SequenceEqual(b)
            select (a, b);

        pairGen.Sample(
            p => !PhoneCallDhValidator.IsGaHashValid(p.b, SHA256.HashData(p.a)),
            iter: 1000);
    }

    /// <summary>
    /// Property 4 edge cases: null / empty inputs are never accepted.
    ///
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public void GaHash_NullOrEmptyInputs_AreRejected()
    {
        var ga = new byte[] { 1, 2, 3 };
        var hash = SHA256.HashData(ga);

        PhoneCallDhValidator.IsGaHashValid(null, hash).ShouldBeFalse();
        PhoneCallDhValidator.IsGaHashValid(Array.Empty<byte>(), hash).ShouldBeFalse();
        PhoneCallDhValidator.IsGaHashValid(ga, null).ShouldBeFalse();
        PhoneCallDhValidator.IsGaHashValid(ga, Array.Empty<byte>()).ShouldBeFalse();
    }
}
