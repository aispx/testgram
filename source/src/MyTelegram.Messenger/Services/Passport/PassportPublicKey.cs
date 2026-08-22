using System.Security.Cryptography;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>Why a public key sent to BotFather was rejected.</summary>
public enum PassportPublicKeyStatus
{
    Valid,

    /// <summary>Parses as a private key — the owner just leaked it and must rotate.</summary>
    PrivateKey,

    /// <summary>Not an RSA public key, or shorter than 2048 bits.</summary>
    Invalid
}

/// <summary>
/// The RSA public key a service uses to receive Telegram Passport credentials. Telegram requires at
/// least 2048 bits; BotFather rejects anything else, and refuses a private key outright.
/// See https://corefork.telegram.org/api/passport
/// </summary>
public static class PassportPublicKey
{
    public const int MinKeySizeBits = 2048;

    /// <summary>
    /// Validates a PEM blob and returns it normalised (canonical SubjectPublicKeyInfo PEM), so keys
    /// stored by different clients compare byte for byte against what account.getAuthorizationForm
    /// receives.
    /// </summary>
    public static PassportPublicKeyStatus TryNormalize(string? pem, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(pem))
        {
            return PassportPublicKeyStatus.Invalid;
        }

        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return PassportPublicKeyStatus.PrivateKey;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);

            if (rsa.KeySize < MinKeySizeBits)
            {
                return PassportPublicKeyStatus.Invalid;
            }

            normalized = rsa.ExportSubjectPublicKeyInfoPem();

            return PassportPublicKeyStatus.Valid;
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            return PassportPublicKeyStatus.Invalid;
        }
    }

    /// <summary>
    /// Compares the key a client quoted from the authorization URI with the key the bot registered.
    /// Both sides are normalised first: clients pass the key through URL encoding and line-ending
    /// rewrites, so a plain string comparison would reject legitimate requests.
    /// </summary>
    public static bool Matches(string? stored, string? provided)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        return TryNormalize(stored, out var normalizedStored) == PassportPublicKeyStatus.Valid
               && TryNormalize(provided, out var normalizedProvided) == PassportPublicKeyStatus.Valid
               && string.Equals(normalizedStored, normalizedProvided, StringComparison.Ordinal);
    }
}
