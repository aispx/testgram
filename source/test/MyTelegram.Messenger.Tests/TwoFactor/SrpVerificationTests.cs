using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using MyTelegram.Core;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.TwoFactor;

/// <summary>
///     Starts one real <c>mongod</c> for the whole SRP test class.
/// </summary>
public sealed class MongoFixture : IDisposable
{
    public MongoFixture()
    {
        if (EmbeddedMongoServer.MongoAvailable)
        {
            Server = EmbeddedMongoServer.Start();
        }
    }

    public EmbeddedMongoServer? Server { get; }

    public void Dispose() => Server?.Dispose();
}

/// <summary>
///     Drives the real <see cref="TwoFactorService" /> against a real MongoDB and an in-memory SRP session
///     cache, with a matching client-side SRP implementation, so the parameter checks required by
///     https://corefork.telegram.org/api/srp are exercised end to end.
/// </summary>
public sealed class SrpVerificationTests(MongoFixture fixture) : IClassFixture<MongoFixture>
{
    private const long UserId = 2010001;
    private const string Password = "correct horse battery staple";

    /// <summary>
    ///     A ≡ 0 (mod p) collapses S to zero whatever the verifier is, making K = SHA256(0^256) a public
    ///     constant. Every other M1 input (p, g, salt1, salt2, srp_B) is published by account.getPassword, so
    ///     with no range check on A an attacker forges a valid M1 and bypasses 2FA without the password.
    /// </summary>
    [RequiresMongoDbTheory]
    [InlineData("zero")]
    [InlineData("p")]
    [InlineData("2p")]
    public async Task VerifySrp_WithADivisibleByP_IsRejected(string which)
    {
        var (service, client) = await CreateAsync();
        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);

        var p = client.P;
        var a = which switch
        {
            "zero" => BigInteger.Zero,
            "p" => p,
            _ => p * 2
        };

        // S = 0 for any of these, so K is the hash of 256 zero bytes.
        var aBytes = SrpClient.ToPaddedBytes(a, 256);
        var forgedM1 = client.ComputeM1(aBytes, srpB, SHA256.HashData(new byte[256]));

        var accepted = await service.VerifySrpAsync(UserId, srpId, aBytes, forgedM1);

        accepted.ShouldBeFalse($"A = {which} is 0 mod p and must be refused");
    }

    [RequiresMongoDbFact]
    public async Task VerifySrp_WithCorrectPassword_Succeeds()
    {
        var (service, client) = await CreateAsync();
        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);

        var (aBytes, m1) = client.Login(Password, srpB);

        (await service.VerifySrpAsync(UserId, srpId, aBytes, m1)).ShouldBeTrue();
    }

    [RequiresMongoDbFact]
    public async Task VerifySrp_WithWrongPassword_IsRejected()
    {
        var (service, client) = await CreateAsync();
        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);

        var (aBytes, m1) = client.Login("not the password", srpB);

        (await service.VerifySrpAsync(UserId, srpId, aBytes, m1)).ShouldBeFalse();
    }

    /// <summary>srp_B - and therefore the secret exponent b - must not be reusable across attempts.</summary>
    [RequiresMongoDbFact]
    public async Task VerifySrp_BurnsTheSession_EvenAfterAFailedAttempt()
    {
        var (service, client) = await CreateAsync();
        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);

        var (badA, badM1) = client.Login("not the password", srpB);
        (await service.VerifySrpAsync(UserId, srpId, badA, badM1)).ShouldBeFalse();

        var (goodA, goodM1) = client.Login(Password, srpB);
        (await service.VerifySrpAsync(UserId, srpId, goodA, goodM1))
            .ShouldBeFalse("the SRP session must be consumed by the first attempt");
    }

    [RequiresMongoDbFact]
    public async Task VerifySrp_WithOversizedA_IsRejected()
    {
        var (service, client) = await CreateAsync();
        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);

        var oversized = RandomNumberGenerator.GetBytes(257);

        (await service.VerifySrpAsync(UserId, srpId, oversized,
            client.ComputeM1(oversized, srpB, SHA256.HashData(new byte[256])))).ShouldBeFalse();
    }

    /// <summary>
    ///     Changing g must also re-stamp the stored algorithm: the verifier the client just sent was derived
    ///     from the g/p advertised as new_algo, so keeping the old pair would store one that never verifies.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task SetPassword_StampsTheCurrentAlgorithm_OverAStaleStoredOne()
    {
        var (service, client) = await CreateAsync();

        // Simulate an account whose password predates the generator change.
        var stale = await service.GetPasswordAsync(UserId);
        stale.ShouldNotBeNull();
        stale.G = 2;
        await fixture.Server!.Database.GetCollection<UserPasswordDocument>("user-password")
            .ReplaceOneAsync(d => d.Id == UserId, stale);

        var (salt1, salt2, verifier) = client.NewVerifier(Password);
        await service.SetPasswordAsync(UserId, salt1, salt2, verifier, hint: null);

        var updated = await service.GetPasswordAsync(UserId);
        updated!.G.ShouldBe(SrpConstants.G);

        var (srpB, srpId) = await service.GenerateSrpParamsAsync(UserId);
        var (aBytes, m1) = client.Login(Password, srpB);
        (await service.VerifySrpAsync(UserId, srpId, aBytes, m1))
            .ShouldBeTrue("a freshly set password must verify against the advertised algorithm");
    }

    /// <summary>
    ///     Official clients run mtproto::DhHandshake::check_config over the advertised (g, p) and return
    ///     inputCheckPasswordEmpty when it fails, so a bad generator makes 2FA unusable, not merely weak.
    /// </summary>
    [Fact]
    public void SrpGenerator_SatisfiesTheGuidelineConditionForItsPrime()
    {
        var p = new BigInteger(SrpConstants.P2048, isUnsigned: true, isBigEndian: true);
        var q = (p - 1) / 2;

        SrpConstants.G.ShouldBe(3);
        ((int)(p % 3)).ShouldBe(2, "g = 3 requires p mod 3 == 2");

        // g must generate the order-q subgroup, i.e. be a quadratic residue: g^q == 1 (mod p).
        BigInteger.ModPow(SrpConstants.G, q, p)
            .ShouldBe(BigInteger.One, "g must generate the subgroup of order (p-1)/2");

        // The previous value, 2, is a non-residue for this prime and fails that condition.
        BigInteger.ModPow(2, q, p).ShouldNotBe(BigInteger.One);
        ((int)(p % 8)).ShouldBe(3, "g = 2 would require p mod 8 == 7");
    }

    [Fact]
    public void SrpPrime_IsTheSameSafePrimeAsTheMtprotoHandshake()
    {
        SrpConstants.P2048.ShouldBe(AuthConsts.Dh2048P);
    }

    private async Task<(TwoFactorService Service, SrpClient Client)> CreateAsync()
    {
        var service = new TwoFactorService(fixture.Server!.Database, new InMemoryCacheManager<SrpSessionCache>());
        var client = new SrpClient();

        var (salt1, salt2, verifier) = client.NewVerifier(Password);
        await service.SetPasswordAsync(UserId, salt1, salt2, verifier, hint: null);

        return (service, client);
    }

    /// <summary>
    ///     The client half of SRP. The server never derives x, it only ever stores and uses the verifier
    ///     v = g^x mod p, so the test only needs a KDF that both halves agree on.
    /// </summary>
    private sealed class SrpClient
    {
        private byte[] _salt1 = [];
        private byte[] _salt2 = [];

        public BigInteger P { get; } = new(SrpConstants.P2048, isUnsigned: true, isBigEndian: true);
        private BigInteger G { get; } = new(SrpConstants.G);

        public (byte[] Salt1, byte[] Salt2, byte[] Verifier) NewVerifier(string password)
        {
            _salt1 = RandomNumberGenerator.GetBytes(32);
            _salt2 = RandomNumberGenerator.GetBytes(32);

            var v = BigInteger.ModPow(G, DeriveX(password), P);

            return (_salt1, _salt2, ToPaddedBytes(v, 256));
        }

        public (byte[] A, byte[] M1) Login(string password, byte[] srpB)
        {
            var x = DeriveX(password);

            var aExp = new BigInteger(RandomNumberGenerator.GetBytes(256), isUnsigned: true, isBigEndian: true) % P;
            var aBytes = ToPaddedBytes(BigInteger.ModPow(G, aExp, P), 256);

            var k = new BigInteger(SHA256.HashData([.. PBytes, .. GBytes]), isUnsigned: true, isBigEndian: true);
            var u = new BigInteger(SHA256.HashData([.. aBytes, .. srpB]), isUnsigned: true, isBigEndian: true);

            // S = (B - k * g^x) ^ (a + u * x) mod p
            var t = (new BigInteger(srpB, isUnsigned: true, isBigEndian: true) - k * BigInteger.ModPow(G, x, P)) % P;
            if (t.Sign < 0)
            {
                t += P;
            }

            var s = BigInteger.ModPow(t, aExp + u * x, P);

            return (aBytes, ComputeM1(aBytes, srpB, SHA256.HashData(ToPaddedBytes(s, 256))));
        }

        public byte[] ComputeM1(byte[] aBytes, byte[] srpB, byte[] kA)
        {
            var hp = SHA256.HashData(PBytes);
            var hg = SHA256.HashData(GBytes);
            var hpXorHg = hp.Zip(hg, (x, y) => (byte)(x ^ y)).ToArray();

            return SHA256.HashData([
                .. hpXorHg,
                .. SHA256.HashData(_salt1),
                .. SHA256.HashData(_salt2),
                .. aBytes,
                .. srpB,
                .. kA
            ]);
        }

        private byte[] PBytes => ToPaddedBytes(P, 256);
        private byte[] GBytes => ToPaddedBytes(G, 256);

        private BigInteger DeriveX(string password)
        {
            var ph1 = SHA256.HashData([.. _salt1, .. Encoding.UTF8.GetBytes(password), .. _salt1]);
            var pbkdf2 = Rfc2898DeriveBytes.Pbkdf2(ph1, _salt2, 4096, HashAlgorithmName.SHA512, 64);

            return new BigInteger(SHA256.HashData([.. _salt2, .. pbkdf2, .. _salt2]),
                isUnsigned: true, isBigEndian: true);
        }

        public static byte[] ToPaddedBytes(BigInteger n, int size)
        {
            var bytes = n.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (bytes.Length >= size)
            {
                return bytes[^size..];
            }

            var padded = new byte[size];
            bytes.CopyTo(padded, size - bytes.Length);

            return padded;
        }
    }

    private sealed class InMemoryCacheManager<T> : ICacheManager<T> where T : class
    {
        private readonly Dictionary<string, T> _items = new();

        public Task<T?> GetAsync(string key) =>
            Task.FromResult(_items.TryGetValue(key, out var value) ? value : null);

        public Task<IDictionary<string, T>> GetManyAsync(IReadOnlyList<string> keys)
        {
            IDictionary<string, T> result = keys.Where(_items.ContainsKey).ToDictionary(k => k, k => _items[k]);

            return Task.FromResult(result);
        }

        public Task RemoveAsync(string key)
        {
            _items.Remove(key);

            return Task.CompletedTask;
        }

        public Task SetAsync(string key, T value, int ttlInSeconds = -1)
        {
            _items[key] = value;

            return Task.CompletedTask;
        }
    }
}
