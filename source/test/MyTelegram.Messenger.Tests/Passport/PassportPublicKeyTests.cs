using System.Security.Cryptography;
using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: the RSA public key a service registers through BotFather's <c>/setpublickey</c>.
///
/// <para>
/// The key is what the user's client encrypts the Passport credentials to, and what the server checks
/// the <c>public_key</c> of <c>account.getAuthorizationForm</c> against. BotFather refuses anything
/// that is not an RSA public key of at least 2048 bits, and calls out a private key specifically.
/// See https://corefork.telegram.org/api/passport
/// </para>
/// </summary>
public class PassportPublicKeyTests
{
    [Fact]
    public void A_2048_bit_public_key_is_accepted()
    {
        using var rsa = RSA.Create(2048);

        var status = PassportPublicKey.TryNormalize(rsa.ExportSubjectPublicKeyInfoPem(), out var normalized);

        status.ShouldBe(PassportPublicKeyStatus.Valid);
        normalized.ShouldContain("BEGIN PUBLIC KEY");
    }

    [Fact]
    public void A_1024_bit_public_key_is_rejected()
    {
        using var rsa = RSA.Create(1024);

        PassportPublicKey.TryNormalize(rsa.ExportSubjectPublicKeyInfoPem(), out _)
            .ShouldBe(PassportPublicKeyStatus.Invalid);
    }

    [Fact]
    public void A_private_key_is_reported_separately()
    {
        using var rsa = RSA.Create(2048);

        // BotFather answers this case with "your private key was compromised", not with the generic
        // "invalid key" message, so the two must not collapse into one status.
        PassportPublicKey.TryNormalize(rsa.ExportPkcs8PrivateKeyPem(), out _)
            .ShouldBe(PassportPublicKeyStatus.PrivateKey);
    }

    [Fact]
    public void A_truncated_private_key_header_is_still_reported_as_a_private_key()
    {
        // The transcript that motivated this: a user pastes just the "-----BEGIN PRIVATE KEY-----" line.
        PassportPublicKey.TryNormalize("-----BEGIN PRIVATE KEY-----", out _)
            .ShouldBe(PassportPublicKeyStatus.PrivateKey);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        PassportPublicKey.TryNormalize("пваыпва", out _).ShouldBe(PassportPublicKeyStatus.Invalid);
        PassportPublicKey.TryNormalize("", out _).ShouldBe(PassportPublicKeyStatus.Invalid);
        PassportPublicKey.TryNormalize(null, out _).ShouldBe(PassportPublicKeyStatus.Invalid);
    }

    [Fact]
    public void A_key_that_only_differs_in_line_endings_still_matches()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();

        // Clients round-trip the key through the authorization URI, which rewrites line endings; a
        // plain string comparison would answer PUBLIC_KEY_REQUIRED for a legitimate request.
        PassportPublicKey.Matches(pem, pem.Replace("\n", "\r\n")).ShouldBeTrue();
    }

    [Fact]
    public void A_different_key_does_not_match()
    {
        using var registered = RSA.Create(2048);
        using var other = RSA.Create(2048);

        PassportPublicKey.Matches(registered.ExportSubjectPublicKeyInfoPem(),
            other.ExportSubjectPublicKeyInfoPem()).ShouldBeFalse();
    }

    [Fact]
    public void A_bot_without_a_registered_key_never_matches()
    {
        using var rsa = RSA.Create(2048);

        PassportPublicKey.Matches(null, rsa.ExportSubjectPublicKeyInfoPem()).ShouldBeFalse();
    }
}
