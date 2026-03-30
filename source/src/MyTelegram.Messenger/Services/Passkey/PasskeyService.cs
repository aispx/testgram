using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passkey;

public interface IPasskeyService
{
    Task<string> CreateRegistrationOptionsAsync(long userId, string userName, int ttlSeconds = 300);
    Task<PasskeyDocument> VerifyRegistrationAsync(long userId, string credentialId, string clientDataJson, byte[] attestationData);
    Task SaveAsync(PasskeyDocument doc);
    Task<string> CreateLoginOptionsAsync(int ttlSeconds = 300);
    Task<PasskeyDocument> VerifyLoginAsync(string credentialId, string clientDataJson, byte[] clientDataRaw, byte[] authenticatorData, byte[] signature, string userHandle);
    Task<List<PasskeyDocument>> GetPasskeysAsync(long userId);
    Task DeletePasskeyAsync(long userId, string id);
    Task UpdateSignCountAsync(string credentialId, uint signCount, int lastUsageDate);
}

public class PasskeyService(
    IMongoDatabase mongoDatabase,
    ICacheManager<string> cacheManager,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options) : IPasskeyService, ISingletonDependency
{
    private const string CollectionName = "passkey-readmodel";
    private IMongoCollection<PasskeyDocument> Collection => mongoDatabase.GetCollection<PasskeyDocument>(CollectionName);

    private string RpId => options.CurrentValue.PasskeyRpId ?? "localhost";
    private string RpName => options.CurrentValue.PasskeyRpName ?? "Testgram";

    public async Task<string> CreateRegistrationOptionsAsync(long userId, string userName, int ttlSeconds = 300)
    {
        var challenge = GenerateChallenge();
        var challengeB64 = Base64UrlEncode(challenge);
        await cacheManager.SetAsync($"passkey:reg:{userId}", challengeB64, ttlSeconds);

        var opts = new
        {
            challenge = challengeB64,
            rp = new { id = RpId, name = RpName },
            user = new { id = Base64UrlEncode(Encoding.UTF8.GetBytes($"{options.CurrentValue.ThisDcId}:{userId}")), name = userName, displayName = userName },
            pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
            timeout = ttlSeconds * 1000,
            attestation = "none",
            authenticatorSelection = new { residentKey = "required", requireResidentKey = true, userVerification = "preferred" }
        };
        var result = System.Text.Json.JsonSerializer.Serialize(new { publicKey = opts });
        return result;
    }

    public async Task<PasskeyDocument> VerifyRegistrationAsync(long userId, string credentialId, string clientDataJson, byte[] attestationData)
    {
        var expectedChallenge = await cacheManager.GetAsync($"passkey:reg:{userId}");
        if (expectedChallenge == null)
            throw new InvalidOperationException("Registration challenge expired or not found");

        var clientData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(clientDataJson);
        if (clientData.GetProperty("challenge").GetString() != expectedChallenge)
            throw new InvalidOperationException("Challenge mismatch");
        if (clientData.GetProperty("type").GetString() != "webauthn.create")
            throw new InvalidOperationException("Invalid type");

        var authData = ParseAttestationAuthData(attestationData);
        var publicKey = ExtractPublicKeyFromAuthData(authData);

        await cacheManager.RemoveAsync($"passkey:reg:{userId}");

        return new PasskeyDocument
        {
            Id = credentialId,
            UserId = userId,
            Name = "Passkey",
            PublicKey = publicKey,
            SignCount = ReadSignCount(authData),
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
        await cacheManager.SetAsync($"passkey:login:{challengeB64}", challengeB64, ttlSeconds);

        var opts = new
        {
            challenge = challengeB64,
            rpId = RpId,
            timeout = ttlSeconds * 1000,
            userVerification = "preferred",
            allowCredentials = Array.Empty<object>()
        };
        return System.Text.Json.JsonSerializer.Serialize(new { publicKey = opts });
    }

    public async Task<PasskeyDocument> VerifyLoginAsync(string credentialId, string clientDataJson, byte[] clientDataRaw, byte[] authenticatorData, byte[] signature, string userHandle)
    {
        var clientData = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(clientDataJson);
        var challenge = clientData.GetProperty("challenge").GetString()!;
        if (clientData.GetProperty("type").GetString() != "webauthn.get")
            throw new InvalidOperationException("Invalid type");

        var cachedChallenge = await cacheManager.GetAsync($"passkey:login:{challenge}");
        if (cachedChallenge == null)
            throw new InvalidOperationException("Login challenge expired or not found");

        var passkey = await Collection.Find(p => p.Id == credentialId).FirstOrDefaultAsync();
        if (passkey == null)
            throw new InvalidOperationException("Passkey not found");

        var clientDataHash = SHA256.HashData(clientDataRaw);
        // clientDataJson may have been decoded from base64url; hash the raw bytes as received
        var signedData = authenticatorData.Concat(clientDataHash).ToArray();
        VerifySignature(passkey.PublicKey, signedData, signature);

        await cacheManager.RemoveAsync($"passkey:login:{challenge}");
        return passkey;
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

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    private static byte[] ParseAttestationAuthData(byte[] attestationData)
    {
        var reader = new CborReader(attestationData);
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            if (key == "authData") return reader.ReadByteString();
            reader.SkipValue();
        }
        throw new InvalidOperationException("authData not found in attestation");
    }

    private static byte[] ExtractPublicKeyFromAuthData(byte[] authData)
    {
        if (authData.Length < 37) throw new InvalidOperationException("authData too short");
        var flags = authData[32];
        if ((flags & 0x40) == 0) throw new InvalidOperationException("No attested credential data");
        var offset = 37 + 16; // skip rpIdHash(32)+flags(1)+signCount(4)+aaguid(16)
        var credIdLen = (authData[offset] << 8) | authData[offset + 1];
        offset += 2 + credIdLen;
        return authData[offset..];
    }

    private static uint ReadSignCount(byte[] authData)
    {
        if (authData.Length < 37) return 0;
        return (uint)((authData[33] << 24) | (authData[34] << 16) | (authData[35] << 8) | authData[36]);
    }

    private static long ReadCborInt(CborReader reader)
    {
        if (reader.PeekState() == CborReaderState.UnsignedInteger)
            return (long)reader.ReadUInt64();
        return reader.ReadInt64();
    }

    private static void VerifySignature(byte[] cosePublicKey, byte[] data, byte[] signature)
    {
        // First pass: collect all key-value pairs
        var reader = new CborReader(cosePublicKey);
        reader.ReadStartMap();
        int alg = -7; // default EC
        byte[]? x = null, y = null, n = null, e = null;
        var entries = new List<(long key, CborReaderState valueType, byte[]? bytes, long intVal)>();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = ReadCborInt(reader);
            var state = reader.PeekState();
            if (state == CborReaderState.ByteString)
                entries.Add((key, state, reader.ReadByteString(), 0));
            else if (state == CborReaderState.UnsignedInteger || state == CborReaderState.NegativeInteger)
                entries.Add((key, state, null, ReadCborInt(reader)));
            else
            { reader.SkipValue(); entries.Add((key, state, null, 0)); }
        }

        // Find alg first
        foreach (var (key, _, _, intVal) in entries)
            if (key == 3) { alg = (int)intVal; break; }

        foreach (var (key, _, bytes, _) in entries)
        {
            switch (key)
            {
                case -1: if (alg == -257) n = bytes; break; // crv for EC (skip), n for RSA
                case -2: if (alg == -257) e = bytes; else x = bytes; break;
                case -3: y = bytes; break;
            }
        }

        var hash = SHA256.HashData(data);

        if (alg == -7 && x != null && y != null)
        {
            using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            if (!ecdsa.VerifyData(data, ConvertDerToRaw(signature, 32), HashAlgorithmName.SHA256))
                throw new InvalidOperationException("Signature verification failed");
        }
        else if (alg == -257 && n != null && e != null)
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
            if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new InvalidOperationException("Signature verification failed");
        }
        else throw new InvalidOperationException($"Unsupported algorithm: {alg}");
    }

    private static byte[] ConvertDerToRaw(byte[] der, int coordSize)
    {
        var offset = 2;
        var rLen = der[offset + 1]; var r = der.Skip(offset + 2).Take(rLen).ToArray(); offset += 2 + rLen;
        var sLen = der[offset + 1]; var s = der.Skip(offset + 2).Take(sLen).ToArray();
        var raw = new byte[coordSize * 2];
        r.CopyTo(raw, coordSize - r.Length);
        s.CopyTo(raw, coordSize * 2 - s.Length);
        return raw;
    }
}
