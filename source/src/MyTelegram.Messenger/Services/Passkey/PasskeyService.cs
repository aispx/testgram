using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passkey;

public interface IPasskeyService
{
    int MaxPasskeysPerAccount { get; }
    Task<string> CreateRegistrationOptionsAsync(long userId, string userName, int ttlSeconds = 300);
    Task<PasskeyDocument> VerifyRegistrationAsync(long userId, string credentialId, string rawCredentialId, string clientDataJson, byte[] attestationData);
    Task SaveAsync(PasskeyDocument doc);
    Task<string> CreateLoginOptionsAsync(int ttlSeconds = 300);
    Task<PasskeyLoginResult> VerifyLoginAsync(string credentialId, string rawCredentialId, string clientDataJson, byte[] clientDataRaw, byte[] authenticatorData, byte[] signature, string userHandle);
    Task<List<PasskeyDocument>> GetPasskeysAsync(long userId);
    Task DeletePasskeyAsync(long userId, string id);
    Task UpdateSignCountAsync(string credentialId, uint signCount, int lastUsageDate);
}

public sealed record PasskeyLoginResult(PasskeyDocument Passkey, uint SignCount, int UserDcId, long UserId);

public class PasskeyService(
    IMongoDatabase mongoDatabase,
    ICacheManager<string> cacheManager,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options) : IPasskeyService, ISingletonDependency
{
    private const string CollectionName = "passkey-readmodel";
    private const string WebAuthnCreateType = "webauthn.create";
    private const string WebAuthnGetType = "webauthn.get";
    private const byte UserPresentFlag = 0x01;
    private const byte AttestedCredentialDataFlag = 0x40;

    private IMongoCollection<PasskeyDocument> Collection => mongoDatabase.GetCollection<PasskeyDocument>(CollectionName);

    // Official Telegram passkeys are scoped to telegram.org as RP ID. Deployments may override it for local/test environments.
    private string RpId => string.IsNullOrWhiteSpace(options.CurrentValue.PasskeyRpId) ? "telegram.org" : options.CurrentValue.PasskeyRpId!;
    private string RpName => string.IsNullOrWhiteSpace(options.CurrentValue.PasskeyRpName) ? "Telegram" : options.CurrentValue.PasskeyRpName!;
    public int MaxPasskeysPerAccount => options.CurrentValue.PasskeysAccountPasskeysMax <= 0 ? 20 : options.CurrentValue.PasskeysAccountPasskeysMax;

    public async Task<string> CreateRegistrationOptionsAsync(long userId, string userName, int ttlSeconds = 300)
    {
        var existingPasskeys = await GetPasskeysAsync(userId);
        if (existingPasskeys.Count >= MaxPasskeysPerAccount)
            throw new InvalidOperationException("Passkey limit reached");

        var challenge = GenerateChallenge();
        var challengeB64 = Base64UrlEncode(challenge);
        await cacheManager.SetAsync(RegistrationChallengeKey(userId), challengeB64, ttlSeconds);

        var opts = new
        {
            challenge = challengeB64,
            rp = new { id = RpId, name = RpName },
            user = new
            {
                id = Base64UrlEncode(Encoding.UTF8.GetBytes($"{options.CurrentValue.ThisDcId}:{userId}")),
                name = userName,
                displayName = userName
            },
            pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
            timeout = ttlSeconds * 1000,
            attestation = "none",
            excludeCredentials = existingPasskeys.Select(p => new { type = "public-key", id = p.Id }).ToArray(),
            authenticatorSelection = new { residentKey = "required", requireResidentKey = true, userVerification = "preferred" }
        };

        return JsonSerializer.Serialize(new { publicKey = opts });
    }

    public async Task<PasskeyDocument> VerifyRegistrationAsync(long userId, string credentialId, string rawCredentialId, string clientDataJson, byte[] attestationData)
    {
        var expectedChallenge = await cacheManager.GetAsync(RegistrationChallengeKey(userId));
        if (expectedChallenge == null)
            throw new InvalidOperationException("Registration challenge expired or not found");

        var clientData = ParseClientData(clientDataJson, WebAuthnCreateType, expectedChallenge);
        ValidateOrigin(clientData);

        var authDataBytes = ParseAttestationAuthData(attestationData);
        var authData = ParseAuthenticatorData(authDataBytes, requireAttestedCredentialData: true);
        ValidateRpIdHash(authData.RpIdHash);
        ValidateUserPresent(authData.Flags);

        var attestedCredentialId = Base64UrlEncode(authData.CredentialId!);
        if (!CredentialIdsMatch(credentialId, rawCredentialId, attestedCredentialId))
            throw new InvalidOperationException("Credential ID mismatch");

        await cacheManager.RemoveAsync(RegistrationChallengeKey(userId));

        return new PasskeyDocument
        {
            Id = credentialId,
            RawId = rawCredentialId,
            UserId = userId,
            Name = "Passkey",
            PublicKey = authData.CosePublicKey!,
            SignCount = authData.SignCount,
            CreatedAt = DateTime.UtcNow.ToTimestamp()
        };
    }

    public async Task SaveAsync(PasskeyDocument doc)
    {
        await Collection.ReplaceOneAsync(
            p => p.Id == doc.Id,
            doc,
            new ReplaceOptions { IsUpsert = true });
    }

    public async Task<string> CreateLoginOptionsAsync(int ttlSeconds = 300)
    {
        var challenge = GenerateChallenge();
        var challengeB64 = Base64UrlEncode(challenge);
        await cacheManager.SetAsync(LoginChallengeKey(challengeB64), challengeB64, ttlSeconds);

        var opts = new
        {
            challenge = challengeB64,
            rpId = RpId,
            timeout = ttlSeconds * 1000,
            userVerification = "preferred",
            allowCredentials = Array.Empty<object>()
        };

        return JsonSerializer.Serialize(new { publicKey = opts });
    }

    public async Task<PasskeyLoginResult> VerifyLoginAsync(string credentialId, string rawCredentialId, string clientDataJson, byte[] clientDataRaw, byte[] authenticatorData, byte[] signature, string userHandle)
    {
        var clientData = JsonSerializer.Deserialize<JsonElement>(clientDataJson);
        if (!clientData.TryGetProperty("challenge", out var challengeProperty))
            throw new InvalidOperationException("Challenge is missing");

        var challenge = challengeProperty.GetString() ?? throw new InvalidOperationException("Challenge is empty");
        ParseClientData(clientDataJson, WebAuthnGetType, challenge);
        ValidateOrigin(clientData);

        var cachedChallenge = await cacheManager.GetAsync(LoginChallengeKey(challenge));
        if (cachedChallenge == null || cachedChallenge != challenge)
            throw new InvalidOperationException("Login challenge expired or not found");

        var passkey = await Collection.Find(p => p.Id == credentialId || p.RawId == rawCredentialId).FirstOrDefaultAsync();
        if (passkey == null)
            throw new InvalidOperationException("Passkey not found");

        var (userDcId, userId) = DecodeUserHandle(userHandle);
        if (userId != passkey.UserId)
            throw new InvalidOperationException("User handle does not match credential owner");

        var parsedAuthData = ParseAuthenticatorData(authenticatorData, requireAttestedCredentialData: false);
        ValidateRpIdHash(parsedAuthData.RpIdHash);
        ValidateUserPresent(parsedAuthData.Flags);

        if (passkey.SignCount > 0 && parsedAuthData.SignCount > 0 && parsedAuthData.SignCount <= passkey.SignCount)
            throw new InvalidOperationException("Authenticator sign counter rollback detected");

        var clientDataHash = SHA256.HashData(clientDataRaw);
        var signedData = new byte[authenticatorData.Length + clientDataHash.Length];
        Buffer.BlockCopy(authenticatorData, 0, signedData, 0, authenticatorData.Length);
        Buffer.BlockCopy(clientDataHash, 0, signedData, authenticatorData.Length, clientDataHash.Length);
        VerifySignature(passkey.PublicKey, signedData, signature);

        await cacheManager.RemoveAsync(LoginChallengeKey(challenge));
        return new PasskeyLoginResult(passkey, parsedAuthData.SignCount, userDcId, userId);
    }

    public async Task<List<PasskeyDocument>> GetPasskeysAsync(long userId) =>
        await Collection.Find(p => p.UserId == userId).ToListAsync();

    public async Task DeletePasskeyAsync(long userId, string id) =>
        await Collection.DeleteOneAsync(p => p.UserId == userId && p.Id == id);

    public async Task UpdateSignCountAsync(string credentialId, uint signCount, int lastUsageDate) =>
        await Collection.UpdateOneAsync(
            p => p.Id == credentialId,
            Builders<PasskeyDocument>.Update
                .Set(p => p.SignCount, signCount)
                .Set(p => p.LastUsageDate, lastUsageDate));

    // --- helpers ---

    private static string RegistrationChallengeKey(long userId) => $"passkey:reg:{userId}";
    private static string LoginChallengeKey(string challenge) => $"passkey:login:{challenge}";

    private static byte[] GenerateChallenge()
    {
        var b = new byte[32];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    // clientData.Data may be raw JSON or base64url-encoded JSON
    public static string DecodeClientDataJson(string data)
    {
        if (data.TrimStart().StartsWith('{'))
            return data;

        try { return Encoding.UTF8.GetString(Base64UrlDecode(data)); }
        catch { return data; }
    }

    public static byte[] DecodeClientDataBytes(string data)
    {
        if (data.TrimStart().StartsWith('{'))
            return Encoding.UTF8.GetBytes(data);

        try { return Base64UrlDecode(data); }
        catch { return Encoding.UTF8.GetBytes(data); }
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 0: break;
            case 2: s += "=="; break;
            case 3: s += "="; break;
            default: throw new FormatException("Invalid base64url length");
        }

        return Convert.FromBase64String(s);
    }

    private JsonElement ParseClientData(string clientDataJson, string expectedType, string expectedChallenge)
    {
        var clientData = JsonSerializer.Deserialize<JsonElement>(clientDataJson);
        if (!clientData.TryGetProperty("type", out var typeProperty) || typeProperty.GetString() != expectedType)
            throw new InvalidOperationException("Invalid clientData type");

        if (!clientData.TryGetProperty("challenge", out var challengeProperty) || challengeProperty.GetString() != expectedChallenge)
            throw new InvalidOperationException("Challenge mismatch");

        return clientData;
    }

    private void ValidateOrigin(JsonElement clientData)
    {
        if (!clientData.TryGetProperty("origin", out var originProperty))
            return;

        var origin = originProperty.GetString();
        if (string.IsNullOrWhiteSpace(origin))
            return;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Invalid WebAuthn origin");

        // Native platform passkeys may use non-HTTP origins; RP-ID hash validation above is the binding check.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return;

        var rpId = RpId;
        if (string.Equals(rpId, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Origin host mismatch");
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WebAuthn origin must be https");

        if (!string.Equals(uri.Host, rpId, StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Origin host mismatch");
    }

    private void ValidateRpIdHash(byte[] rpIdHash)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(RpId));
        if (!CryptographicOperations.FixedTimeEquals(expected, rpIdHash))
            throw new InvalidOperationException("RP ID hash mismatch");
    }

    private static void ValidateUserPresent(byte flags)
    {
        if ((flags & UserPresentFlag) == 0)
            throw new InvalidOperationException("User presence flag is missing");
    }

    private static bool CredentialIdsMatch(string credentialId, string rawCredentialId, string attestedCredentialId)
    {
        if (credentialId == attestedCredentialId || rawCredentialId == attestedCredentialId)
            return true;

        return TryNormalizeCredentialId(credentialId, out var normalizedCredentialId) && normalizedCredentialId == attestedCredentialId ||
               TryNormalizeCredentialId(rawCredentialId, out var normalizedRawId) && normalizedRawId == attestedCredentialId;
    }

    private static bool TryNormalizeCredentialId(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            normalized = Base64UrlEncode(Base64UrlDecode(value));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int DcId, long UserId) DecodeUserHandle(string userHandle)
    {
        var decoded = userHandle;
        if (!decoded.Contains(':'))
        {
            decoded = Encoding.UTF8.GetString(Base64UrlDecode(userHandle));
        }

        var parts = decoded.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var dcId) || !long.TryParse(parts[1], out var userId))
            throw new InvalidOperationException("Invalid passkey user handle");

        return (dcId, userId);
    }

    private static byte[] ParseAttestationAuthData(byte[] attestationData)
    {
        var reader = new CborReader(attestationData);
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            if (key == "authData")
                return reader.ReadByteString();

            reader.SkipValue();
        }

        throw new InvalidOperationException("authData not found in attestation");
    }

    private static AuthenticatorData ParseAuthenticatorData(byte[] authData, bool requireAttestedCredentialData)
    {
        if (authData.Length < 37)
            throw new InvalidOperationException("authData too short");

        var rpIdHash = authData[..32];
        var flags = authData[32];
        var signCount = ReadSignCount(authData);

        if (!requireAttestedCredentialData)
            return new AuthenticatorData(rpIdHash, flags, signCount, null, null);

        if ((flags & AttestedCredentialDataFlag) == 0)
            throw new InvalidOperationException("No attested credential data");

        var offset = 37 + 16; // rpIdHash(32)+flags(1)+signCount(4)+aaguid(16)
        if (authData.Length < offset + 2)
            throw new InvalidOperationException("credential id length is missing");

        var credIdLen = (authData[offset] << 8) | authData[offset + 1];
        offset += 2;
        if (authData.Length < offset + credIdLen)
            throw new InvalidOperationException("credential id is truncated");

        var credentialId = authData[offset..(offset + credIdLen)];
        offset += credIdLen;
        if (authData.Length <= offset)
            throw new InvalidOperationException("COSE public key is missing");

        return new AuthenticatorData(rpIdHash, flags, signCount, credentialId, authData[offset..]);
    }

    private static uint ReadSignCount(byte[] authData) =>
        (uint)((authData[33] << 24) | (authData[34] << 16) | (authData[35] << 8) | authData[36]);

    private static long ReadCborInt(CborReader reader)
    {
        if (reader.PeekState() == CborReaderState.UnsignedInteger)
            return (long)reader.ReadUInt64();

        return reader.ReadInt64();
    }

    private static void VerifySignature(byte[] cosePublicKey, byte[] data, byte[] signature)
    {
        var key = ReadCoseKey(cosePublicKey);

        if (key.Alg == -7 && key.X != null && key.Y != null)
        {
            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = key.X, Y = key.Y }
            });

            if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence) ||
                ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return;

            throw new InvalidOperationException("Signature verification failed");
        }

        if (key.Alg == -257 && key.N != null && key.E != null)
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = key.N, Exponent = key.E });
            if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("Signature verification failed");
            return;
        }

        throw new InvalidOperationException($"Unsupported algorithm: {key.Alg}");
    }

    private static CoseKey ReadCoseKey(byte[] cosePublicKey)
    {
        var reader = new CborReader(cosePublicKey);
        reader.ReadStartMap();

        var entries = new List<(long Key, CborReaderState State, byte[]? Bytes, long IntValue)>();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var mapKey = ReadCborInt(reader);
            var state = reader.PeekState();
            if (state == CborReaderState.ByteString)
            {
                entries.Add((mapKey, state, reader.ReadByteString(), 0));
            }
            else if (state is CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger)
            {
                entries.Add((mapKey, state, null, ReadCborInt(reader)));
            }
            else
            {
                reader.SkipValue();
                entries.Add((mapKey, state, null, 0));
            }
        }

        var alg = entries.FirstOrDefault(p => p.Key == 3).IntValue;
        if (alg == 0)
            alg = -7;

        byte[]? x = null;
        byte[]? y = null;
        byte[]? n = null;
        byte[]? e = null;

        foreach (var (mapKey, _, bytes, _) in entries)
        {
            switch (mapKey)
            {
                case -1 when alg == -257:
                    n = bytes;
                    break;
                case -2 when alg == -257:
                    e = bytes;
                    break;
                case -2:
                    x = bytes;
                    break;
                case -3:
                    y = bytes;
                    break;
            }
        }

        return new CoseKey((int)alg, x, y, n, e);
    }

    private sealed record AuthenticatorData(byte[] RpIdHash, byte Flags, uint SignCount, byte[]? CredentialId, byte[]? CosePublicKey);
    private sealed record CoseKey(int Alg, byte[]? X, byte[]? Y, byte[]? N, byte[]? E);
}
