using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Services.Services;

namespace MyTelegram.AuthServer.Tests;

/// <summary>
///     Wires up the real <see cref="Step2Helper" /> / <see cref="Step3Helper" /> against real crypto helpers
///     and an in-memory cache, and provides the matching client-side crypto so a full auth key handshake can be
///     driven end to end (including deliberately malformed handshakes).
/// </summary>
internal sealed class HandshakeTestContext
{
    public HandshakeTestContext()
    {
        var rsa = RSA.Create(2048);
        PrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        var publicParameters = rsa.ExportParameters(false);
        RsaModulus = new BigInteger(publicParameters.Modulus!, true, true);
        RsaExponent = new BigInteger(publicParameters.Exponent!, true, true);

        AesHelper = new AesHelper();
        var hashHelper = new HashHelper();
        MtpHelper = new MtpHelper(AesHelper);
        var rsaHelper = new MyRsaHelper();
        var keyProvider = new FakeRsaKeyProvider(PrivateKey);
        FingerprintHelper = new FingerprintHelper(keyProvider, rsaHelper);
        Cache = new InMemoryCacheManager<AuthCacheItem>();

        Step2 = new Step2Helper(
            NullLogger<Step2Helper>.Instance,
            AesHelper,
            MtpHelper,
            rsaHelper,
            Cache,
            keyProvider,
            FingerprintHelper);

        Step3 = new Step3Helper(
            AesHelper,
            hashHelper,
            MtpHelper,
            NullLogger<Step3Helper>.Instance,
            new AuthKeyIdHelper(),
            Cache);
    }

    public AesHelper AesHelper { get; }
    public InMemoryCacheManager<AuthCacheItem> Cache { get; }
    public FingerprintHelper FingerprintHelper { get; }
    public MtpHelper MtpHelper { get; }
    public string PrivateKey { get; }
    public BigInteger RsaExponent { get; }
    public BigInteger RsaModulus { get; }
    public Step2Helper Step2 { get; }
    public Step3Helper Step3 { get; }

    /// <summary>Seeds the cache with the state <c>req_DH_params</c> would have left behind.</summary>
    public HandshakeState SeedPostStep2State()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var serverNonce = RandomNumberGenerator.GetBytes(16);
        var newNonce = RandomNumberGenerator.GetBytes(32);
        var a = RandomNumberGenerator.GetBytes(256);

        var item = new AuthCacheItem(
            nonce,
            serverNonce,
            AuthConsts.P,
            AuthConsts.Q,
            true,
            newNonce,
            a,
            BigInteger.ModPow(AuthConsts.G3, a.ToBigEndianBigInteger(), AuthConsts.DhPrime).ToByteArray(true, true));

        Cache.SetAsync(AuthCacheItem.GetCacheKey(serverNonce), item).GetAwaiter().GetResult();

        return new HandshakeState(nonce, serverNonce, newNonce, a);
    }

    /// <summary>Builds a <c>set_client_DH_params</c> request the way a client would.</summary>
    public RequestSetClientDHParams BuildSetClientDhParams(
        HandshakeState state,
        byte[] gb,
        long retryId = 0,
        bool corruptHash = false,
        int extraPadding = 0)
    {
        var innerData = new TClientDHInnerData
        {
            Nonce = state.Nonce,
            ServerNonce = state.ServerNonce,
            RetryId = retryId,
            GB = gb
        };

        using var writer = new ArrayPoolBufferWriter<byte>();
        innerData.Serialize(writer);
        var answer = writer.WrittenSpan.ToArray();

        var hash = SHA1.HashData(answer);
        if (corruptHash)
        {
            hash[0] ^= 0xff;
        }

        // answer_with_hash := SHA1(answer) + answer + (0-15 random bytes), AES-IGE needs a multiple of 16.
        // extraPadding pushes past the permitted 0-15 so the server-side check can be exercised.
        var unpadded = 20 + answer.Length;
        var totalLength = unpadded + extraPadding;
        if (totalLength % 16 != 0)
        {
            totalLength += 16 - (totalLength % 16);
        }

        var answerWithHash = new byte[totalLength];
        hash.CopyTo(answerWithHash, 0);
        answer.CopyTo(answerWithHash, 20);
        RandomNumberGenerator.Fill(answerWithHash.AsSpan(unpadded));

        var aesKey = new byte[32];
        Span<byte> aesIv = stackalloc byte[32];
        MtpHelper.CalcTempAesKeyData(state.NewNonce, state.ServerNonce, aesKey, aesIv);

        var encrypted = new byte[answerWithHash.Length];
        AesHelper.EncryptIge(answerWithHash, aesKey, aesIv, encrypted);

        return new RequestSetClientDHParams
        {
            Nonce = state.Nonce,
            ServerNonce = state.ServerNonce,
            EncryptedData = encrypted
        };
    }

    /// <summary>Builds a <c>req_DH_params</c> request using the modern RSA_PAD encoding.</summary>
    public RequestReqDHParams BuildReqDhParams(byte[] nonce, byte[] serverNonce, byte[] newNonce)
    {
        var innerData = new TPQInnerData
        {
            Pq = AuthConsts.Pq,
            P = AuthConsts.P,
            Q = AuthConsts.Q,
            Nonce = nonce,
            ServerNonce = serverNonce,
            NewNonce = newNonce
        };

        using var writer = new ArrayPoolBufferWriter<byte>();
        innerData.Serialize(writer);
        var data = writer.WrittenSpan.ToArray();

        return new RequestReqDHParams
        {
            Nonce = nonce,
            ServerNonce = serverNonce,
            P = AuthConsts.P,
            Q = AuthConsts.Q,
            PublicKeyFingerprint = FingerprintHelper.GetFingerprint(),
            EncryptedData = RsaPadEncrypt(data)
        };
    }

    /// <summary>
    ///     Builds a <c>req_DH_params</c> carrying an arbitrary, possibly malformed, p_q_inner_data body so the
    ///     server-side deserialization bounds can be exercised.
    /// </summary>
    public RequestReqDHParams BuildReqDhParamsWithRawInnerData(byte[] nonce, byte[] serverNonce, byte[] rawInnerData)
    {
        return new RequestReqDHParams
        {
            Nonce = nonce,
            ServerNonce = serverNonce,
            P = AuthConsts.P,
            Q = AuthConsts.Q,
            PublicKeyFingerprint = FingerprintHelper.GetFingerprint(),
            EncryptedData = RsaPadEncrypt(rawInnerData)
        };
    }

    /// <summary>Decrypts a <c>server_DH_params_ok</c> answer with the temp AES key the client derives.</summary>
    public byte[] DecryptServerDhAnswer(byte[] encryptedAnswer, byte[] newNonce, byte[] serverNonce)
    {
        var aesKey = new byte[32];
        Span<byte> aesIv = stackalloc byte[32];
        MtpHelper.CalcTempAesKeyData(newNonce, serverNonce, aesKey, aesIv);

        var decrypted = new byte[encryptedAnswer.Length];
        AesHelper.DecryptIge(encryptedAnswer, aesKey, aesIv, decrypted);

        return decrypted;
    }

    /// <summary>https://corefork.telegram.org/mtproto/auth_key#41-rsa-pad-data-server-public-key-mentioned-above-is-used</summary>
    private byte[] RsaPadEncrypt(byte[] data)
    {
        while (true)
        {
            var dataWithPadding = new byte[192];
            data.CopyTo(dataWithPadding, 0);
            RandomNumberGenerator.Fill(dataWithPadding.AsSpan(data.Length));

            var dataPadReversed = dataWithPadding.Reverse().ToArray();
            var tempKey = RandomNumberGenerator.GetBytes(32);

            var dataWithHash = new byte[224];
            dataPadReversed.CopyTo(dataWithHash, 0);
            SHA256.HashData([.. tempKey, .. dataWithPadding]).CopyTo(dataWithHash, 192);

            var aesEncrypted = new byte[224];
            Span<byte> zeroIv = stackalloc byte[32];
            AesHelper.EncryptIge(dataWithHash, tempKey, zeroIv, aesEncrypted);

            var aesHash = SHA256.HashData(aesEncrypted);
            var keyAesEncrypted = new byte[256];
            for (var i = 0; i < 32; i++)
            {
                keyAesEncrypted[i] = (byte)(tempKey[i] ^ aesHash[i]);
            }

            aesEncrypted.CopyTo(keyAesEncrypted, 32);

            var value = new BigInteger(keyAesEncrypted, true, true);
            if (value >= RsaModulus)
            {
                continue;
            }

            return BigInteger.ModPow(value, RsaExponent, RsaModulus).ToByteArray(true, true).ToBytes256();
        }
    }

    internal sealed record HandshakeState(byte[] Nonce, byte[] ServerNonce, byte[] NewNonce, byte[] A);

    private sealed class FakeRsaKeyProvider(string privateKey) : IRsaKeyProvider
    {
        public string GetRsaPrivateKey() => privateKey;
    }

    internal sealed class InMemoryCacheManager<T> : ICacheManager<T> where T : class
    {
        private readonly Dictionary<string, T> _items = new();

        public int RemoveCount { get; private set; }

        public Task<T?> GetAsync(string key)
        {
            return Task.FromResult(_items.TryGetValue(key, out var value) ? value : null);
        }

        public Task<IDictionary<string, T>> GetManyAsync(IReadOnlyList<string> keys)
        {
            IDictionary<string, T> result = keys
                .Where(_items.ContainsKey)
                .ToDictionary(k => k, k => _items[k]);

            return Task.FromResult(result);
        }

        public Task RemoveAsync(string key)
        {
            if (_items.Remove(key))
            {
                RemoveCount++;
            }

            return Task.CompletedTask;
        }

        public Task SetAsync(string key, T value, int ttlInSeconds = -1)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }
    }
}
