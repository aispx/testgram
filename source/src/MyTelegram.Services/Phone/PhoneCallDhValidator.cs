using System.Numerics;
using System.Security.Cryptography;
using MyTelegram.Core;

namespace MyTelegram.Services.Phone;

/// <summary>
/// Validates Diffie-Hellman handshake values (<c>g_a</c>, <c>g_b</c>, <c>g_a_hash</c>) relayed during 1:1 calls.
/// The server never learns the shared key; it only checks the structural/range correctness of the values it relays.
/// </summary>
/// <remarks>
/// Range rules (per https://core.telegram.org/mtproto/auth_key#dh-key-exchange-complete):
/// both sides must verify that <c>g</c>, <c>g_a</c> and <c>g_b</c> are greater than 1 and less than <c>p - 1</c>,
/// and additionally that they lie within <c>[2^{2048-64}, p - 2^{2048-64}]</c>.
/// </remarks>
public static class PhoneCallDhValidator
{
    /// <summary>The 2048-bit DH prime <c>p</c> shared with clients via <c>messages.getDhConfig</c>.</summary>
    private static readonly BigInteger Prime = AuthConsts.DhPrime;

    /// <summary>The additional safety bound <c>2^{2048-64}</c>.</summary>
    private static readonly BigInteger SafetyRange = BigInteger.One << (2048 - 64);

    /// <summary>The upper safety bound <c>p - 2^{2048-64}</c>.</summary>
    private static readonly BigInteger UpperSafetyBound = Prime - SafetyRange;

    /// <summary>The lower plain bound (exclusive), i.e. <c>1</c>.</summary>
    private static readonly BigInteger One = BigInteger.One;

    /// <summary>The upper plain bound (exclusive), i.e. <c>p - 1</c>.</summary>
    private static readonly BigInteger PrimeMinusOne = Prime - BigInteger.One;

    /// <summary>
    /// Returns <c>true</c> when the supplied big-endian unsigned DH value satisfies both
    /// <c>1 &lt; g &lt; p - 1</c> and the <c>2^{2048-64}</c> safety bound.
    /// </summary>
    public static bool IsValidDhValue(byte[]? value)
    {
        if (value == null || value.Length == 0)
        {
            return false;
        }

        var g = new BigInteger(value, isUnsigned: true, isBigEndian: true);

        if (g <= One || g >= PrimeMinusOne)
        {
            return false;
        }

        return g >= SafetyRange && g <= UpperSafetyBound;
    }

    /// <summary>
    /// Returns <c>true</c> when <c>SHA256(g_a)</c> equals the previously recorded <c>g_a_hash</c>.
    /// </summary>
    public static bool IsGaHashValid(byte[]? ga, byte[]? gaHash)
    {
        if (ga == null || ga.Length == 0 || gaHash == null || gaHash.Length == 0)
        {
            return false;
        }

        var computed = SHA256.HashData(ga);
        return CryptographicOperations.FixedTimeEquals(computed, gaHash);
    }
}
