using System.Security.Cryptography;

namespace MyTelegram.Core;

/// <summary>
///     All randomness handed out here ends up in security-sensitive places - QR login tokens
///     (auth.exportLoginToken), exported authorizations (auth.exportAuthorization), the future-auth token
///     issued on logout, SMS verification codes and peer access hashes. Random.Shared is xoshiro256**, a
///     non-cryptographic PRNG whose internal state can be recovered from a handful of observed outputs, so
///     every method delegates to the system CSPRNG instead.
/// </summary>
public class RandomHelper : IRandomHelper, ISingletonDependency
{
    private const string Characters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string NumberCharacters = "0123456789";

    public string GenerateRandomNumber(int length)
    {
        return GenerateRandomString(length, NumberCharacters);
    }

    public string GenerateRandomString(int length)
    {
        return GenerateRandomString(length, Characters);
    }

    public void NextBytes(byte[] buffer)
    {
        RandomNumberGenerator.Fill(buffer);
    }

    public byte[] NextBytes(int length)
    {
        return RandomNumberGenerator.GetBytes(length);
    }

    public int NextInt(int min,
        int max)
    {
        return RandomNumberGenerator.GetInt32(min, max);
    }

    public int NextInt()
    {
        return RandomNumberGenerator.GetInt32(int.MaxValue);
    }

    public long NextInt64()
    {
        return BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>
    ///     Picks characters with <see cref="RandomNumberGenerator.GetInt32(int)" />, which rejects biased
    ///     samples internally - a plain modulo over random bytes would skew short verification codes.
    /// </summary>
    private static string GenerateRandomString(int length, string alphabet)
    {
        return string.Create(length, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }
}
