using System.Numerics;
using System.Security.Cryptography;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.TwoFactor;

public interface ITwoFactorService
{
    Task<UserPasswordDocument?> GetPasswordAsync(long userId);
    Task SetPasswordAsync(long userId, byte[] newAlgoSalt1, byte[] newAlgoSalt2, byte[] newPasswordHash, string? hint);
    Task RemovePasswordAsync(long userId);
    Task<(byte[] SrpB, long SrpId)> GenerateSrpParamsAsync(long userId);
    Task<bool> VerifySrpAsync(long userId, long srpId, byte[] a, byte[] m1);
    Task<long?> GetUserIdBySrpIdAsync(long srpId);
    Task SetRecoveryEmailAsync(long userId, string email, string code);
    Task<bool> ConfirmRecoveryEmailAsync(long userId, string code);
    Task CancelRecoveryEmailAsync(long userId);
    Task<string?> GetRecoveryEmailAsync(long userId);
    Task<bool> RequestPasswordResetAsync(long userId);
    Task DeclinePasswordResetAsync(long userId);
    Task<bool> CanResetPasswordAsync(long userId);
    Task StartPasswordResetAsync(long userId);
    Task<DateTime?> GetPasswordResetStateAsync(long userId);
    Task ClearPasswordResetStateAsync(long userId);
    Task<bool> HasPendingRecoveryEmailCodeAsync(long userId);
}

public class TwoFactorService(IMongoDatabase mongoDatabase, ICacheManager<SrpSessionCache> cacheManager)
    : ITwoFactorService, ISingletonDependency
{
    private const string CollectionName = "user-password";
    private IMongoCollection<UserPasswordDocument> Collection =>
        mongoDatabase.GetCollection<UserPasswordDocument>(CollectionName);

    public async Task<UserPasswordDocument?> GetPasswordAsync(long userId) =>
        await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();

    public async Task SetPasswordAsync(long userId, byte[] salt1, byte[] salt2, byte[] passwordHash, string? hint)
    {
        var existing = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        var doc = new UserPasswordDocument
        {
            Id = userId,
            Salt1 = salt1,
            Salt2 = salt2,
            PasswordHash = passwordHash,
            Hint = hint,
            G = existing?.G ?? SrpConstants.G,
            P = existing?.P ?? SrpConstants.P2048,
            RecoveryEmail = existing?.RecoveryEmail,
            RecoveryEmailCode = existing?.RecoveryEmailCode,
            RecoveryEmailCodeExpire = existing?.RecoveryEmailCodeExpire,
            IsPasswordResetRequested = existing?.IsPasswordResetRequested ?? false,
            PasswordResetRequestedAt = existing?.PasswordResetRequestedAt
        };
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc, new ReplaceOptions { IsUpsert = true });
    }

    public async Task RemovePasswordAsync(long userId) =>
        await Collection.DeleteOneAsync(p => p.Id == userId);

    public async Task<(byte[] SrpB, long SrpId)> GenerateSrpParamsAsync(long userId)
    {
        var doc = await GetPasswordAsync(userId) ?? throw new InvalidOperationException("No password set");

        var p = new BigInteger(doc.P, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(doc.G);
        var v = new BigInteger(doc.PasswordHash, isUnsigned: true, isBigEndian: true);

        // k = H(p | g)
        var pBytes = ToPaddedBytes(p, 256);
        var gBytes = ToPaddedBytes(g, 256);
        var k = new BigInteger(SHA256.HashData([.. pBytes, .. gBytes]), isUnsigned: true, isBigEndian: true);

        // b = random 256 bytes
        var bBytes = new byte[256];
        RandomNumberGenerator.Fill(bBytes);
        var b = new BigInteger(bBytes, isUnsigned: true, isBigEndian: true) % p;

        // g_b = (k*v + g^b mod p) mod p
        var gb = BigInteger.ModPow(g, b, p);
        var srpB = ((k * v % p) + gb) % p;

        var srpId = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8));
        var session = new SrpSessionCache { B = ToPaddedBytes(b, 256), SrpId = srpId, UserId = userId };
        await cacheManager.SetAsync($"srp:{srpId}", session, 300);

        return (ToPaddedBytes(srpB, 256), srpId);
    }

    public async Task<long?> GetUserIdBySrpIdAsync(long srpId)
    {
        var session = await cacheManager.GetAsync($"srp:{srpId}");
        return session?.UserId;
    }

    public async Task<bool> VerifySrpAsync(long userId, long srpId, byte[] aBytes, byte[] m1Bytes)
    {
        var session = await cacheManager.GetAsync($"srp:{srpId}");
        if (session == null || session.UserId != userId) return false;

        var doc = await GetPasswordAsync(userId);
        if (doc == null) return false;

        var p = new BigInteger(doc.P, isUnsigned: true, isBigEndian: true);
        var g = new BigInteger(doc.G);
        var v = new BigInteger(doc.PasswordHash, isUnsigned: true, isBigEndian: true);
        var b = new BigInteger(session.B, isUnsigned: true, isBigEndian: true);
        var a = new BigInteger(aBytes, isUnsigned: true, isBigEndian: true);

        var pBytes = ToPaddedBytes(p, 256);
        var gBytes = ToPaddedBytes(g, 256);
        var k = new BigInteger(SHA256.HashData([.. pBytes, .. gBytes]), isUnsigned: true, isBigEndian: true);

        var gb = BigInteger.ModPow(g, b, p);
        var srpB = ((k * v % p) + gb) % p;
        var srpBBytes = ToPaddedBytes(srpB, 256);

        // u = H(A | B)
        var u = new BigInteger(SHA256.HashData([.. aBytes, .. srpBBytes]), isUnsigned: true, isBigEndian: true);

        // S = (A * v^u mod p)^b mod p
        var vu = BigInteger.ModPow(v, u, p);
        var s = BigInteger.ModPow(a * vu % p, b, p);
        var sBytes = ToPaddedBytes(s, 256);

        // K = H(S)
        var kA = SHA256.HashData(sBytes);

        // M1 = H(H(p) xor H(g) | H(salt1) | H(salt2) | A | B | K)
        var hp = SHA256.HashData(pBytes);
        var hg = SHA256.HashData(gBytes);
        var hpXorHg = hp.Zip(hg, (x, y) => (byte)(x ^ y)).ToArray();
        var expectedM1 = SHA256.HashData([
            .. hpXorHg,
            .. SHA256.HashData(doc.Salt1),
            .. SHA256.HashData(doc.Salt2),
            .. aBytes,
            .. srpBBytes,
            .. kA
        ]);

        var ok = expectedM1.SequenceEqual(m1Bytes);
        if (ok) await cacheManager.RemoveAsync($"srp:{srpId}");
        return ok;
    }

    public async Task SetRecoveryEmailAsync(long userId, string email, string code)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return;
        doc.RecoveryEmail = email;
        doc.RecoveryEmailCode = code;
        doc.RecoveryEmailCodeExpire = DateTime.UtcNow.Add(TimeSpan.FromMinutes(5));
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
    }

    public async Task<bool> ConfirmRecoveryEmailAsync(long userId, string code)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null || doc.RecoveryEmailCode != code) return false;
        if (doc.RecoveryEmailCodeExpire.HasValue && doc.RecoveryEmailCodeExpire.Value < DateTime.UtcNow) return false;
        doc.RecoveryEmailCode = null;
        doc.RecoveryEmailCodeExpire = null;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
        return true;
    }

    public async Task CancelRecoveryEmailAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return;
        doc.RecoveryEmail = null;
        doc.RecoveryEmailCode = null;
        doc.RecoveryEmailCodeExpire = null;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
    }

    public async Task<string?> GetRecoveryEmailAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        return doc?.RecoveryEmail;
    }

    public async Task<bool> CanResetPasswordAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null || string.IsNullOrEmpty(doc.RecoveryEmail)) return false;
        if (!doc.PasswordResetRequestedAt.HasValue) return false;
        var daysSinceRequested = (DateTime.UtcNow - doc.PasswordResetRequestedAt.Value).TotalDays;
        return daysSinceRequested <= 7;
    }

    public async Task<bool> RequestPasswordResetAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null || string.IsNullOrEmpty(doc.RecoveryEmail)) return false;
        doc.IsPasswordResetRequested = true;
        doc.PasswordResetRequestedAt = DateTime.UtcNow;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
        return true;
    }

    public async Task DeclinePasswordResetAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return;
        doc.IsPasswordResetRequested = false;
        doc.PasswordResetRequestedAt = null;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
    }

    public async Task StartPasswordResetAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return;
        doc.IsPasswordResetRequested = true;
        doc.PasswordResetRequestedAt = DateTime.UtcNow;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
    }

    public async Task<DateTime?> GetPasswordResetStateAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null || !doc.IsPasswordResetRequested || !doc.PasswordResetRequestedAt.HasValue)
            return null;
        return doc.PasswordResetRequestedAt.Value;
    }

    public async Task ClearPasswordResetStateAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return;
        doc.IsPasswordResetRequested = false;
        doc.PasswordResetRequestedAt = null;
        await Collection.ReplaceOneAsync(p => p.Id == userId, doc);
    }

    public async Task<bool> HasPendingRecoveryEmailCodeAsync(long userId)
    {
        var doc = await Collection.Find(p => p.Id == userId).FirstOrDefaultAsync();
        if (doc == null) return false;
        if (string.IsNullOrEmpty(doc.RecoveryEmailCode)) return false;
        if (doc.RecoveryEmailCodeExpire.HasValue && doc.RecoveryEmailCodeExpire.Value < DateTime.UtcNow) return false;
        return true;
    }

    private static byte[] ToPaddedBytes(BigInteger n, int size)
    {
        var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length >= size) return bytes[^size..];
        var padded = new byte[size];
        bytes.CopyTo(padded, size - bytes.Length);
        return padded;
    }
}

public class SrpSessionCache
{
    public byte[] B { get; set; } = null!;
    public long SrpId { get; set; }
    public long UserId { get; set; }
}
