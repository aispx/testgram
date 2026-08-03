namespace MyTelegram.AuthServer.Services;

public class Step2Helper(
    ILogger<Step2Helper> logger,
    IAesHelper aesHelper,
    IMtpHelper mtpHelper,
    IMyRsaHelper myRsaHelper,
    ICacheManager<AuthCacheItem> cacheManager,
    IRsaKeyProvider rsaKeyProvider,
    IFingerprintHelper fingerprintHelper
) : Step1To3Helper, IStep2Helper, ISingletonDependency
{
    public async Task<Step2Output> GetServerDhParamsAsync(RequestReqDHParams req)
    {
        var cacheKey = GetAuthCacheKey(req.ServerNonce);
        var cachedAuthKey = await cacheManager.GetAsync(cacheKey);
        if (cachedAuthKey == null)
        {
            throw new InvalidOperationException(
                $"GetServerDhParamsAsync: can not find cached auth key info, nonce={req.Nonce.ToHexString()}"
            );
        }

        #region check request

        // The client tells us which server public key it encrypted encrypted_data with; it must be one we
        // actually hold, otherwise the RSA step is not bound to this server's key at all.
        var expectedFingerprint = fingerprintHelper.GetFingerprint();
        if (req.PublicKeyFingerprint != expectedFingerprint)
        {
            throw new DhHandshakeRejectedException(
                $"Unknown public key fingerprint: {req.PublicKeyFingerprint:x}."
            );
        }

        CheckRequestData(cachedAuthKey.Nonce, req.Nonce);
        CheckRequestData(cachedAuthKey.ServerNonce, req.ServerNonce);
        CheckRequestData(cachedAuthKey.P, req.P);
        CheckRequestData(cachedAuthKey.Q, req.Q);

        var tInnerData = DeserializeRequestTpqInnerData(req, rsaKeyProvider.GetRsaPrivateKey());
        CheckRequestData(cachedAuthKey.P, tInnerData.P);
        CheckRequestData(cachedAuthKey.Q, tInnerData.Q);
        CheckRequestData(cachedAuthKey.ServerNonce, tInnerData.ServerNonce);
        CheckRequestData(cachedAuthKey.Nonce, tInnerData.Nonce);

        #endregion check request

        var isPermanentAuthKey = false;
        int? dcId = null;
        switch (tInnerData)
        {
            case TPQInnerData:
                isPermanentAuthKey = true;
                break;
            case TPQInnerDataDc:
                isPermanentAuthKey = true;
                break;
            case TPQInnerDataTemp:

                break;
            case TPQInnerDataTempDc pqInnerDataTempDc:
                dcId = pqInnerDataTempDc.Dc;
                break;
        }

        var dh2048P = AuthConsts.Dh2048P;
        var g = AuthConsts.G;
        var aAndGa = GenerateAAndGa();

        var newCachedAuthKey = cachedAuthKey with
        {
            IsPermanent = isPermanentAuthKey,
            NewNonce = tInnerData.NewNonce,
            A = aAndGa.a,
            Ga = aAndGa.ga,
            DcId = dcId
        };

        var serverDhInnerData = new TServerDHInnerData
        {
            DhPrime = dh2048P,
            G = g[0],
            GA = aAndGa.ga,
            Nonce = cachedAuthKey.Nonce,
            ServerNonce = cachedAuthKey.ServerNonce,
            ServerTime = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await cacheManager.SetAsync(cacheKey, newCachedAuthKey, 600);

        var serverDhParams = SerializeResponse(tInnerData, serverDhInnerData);

        return new Step2Output(tInnerData.NewNonce, serverDhParams);
    }

    private IPQInnerData DeserializeRequestTpqInnerData(
        RequestReqDHParams reqDhParams,
        string privateKey
    )
    {
        // RSA output is always modulus-width, but MyRsaHelper returns a BigInteger encoding with leading
        // zeroes stripped. Normalise back to 256 bytes so the encoding width - which is what distinguishes
        // the two payload formats - survives.
        var innerDataWithHash = myRsaHelper.Decrypt(reqDhParams.EncryptedData, privateKey).ToBytes256();

        // New-style RSA_PAD payloads occupy all 256 bytes. The legacy SHA1(data)+data+padding encoding is
        // 255 bytes wide, so it is always zero-prefixed - only then is the legacy fallback even possible.
        try
        {
            return ParsePqInnerData(innerDataWithHash);
        }
        catch (DhHandshakeRejectedException) when (innerDataWithHash[0] == 0)
        {
            return ParsePqInnerDataOld(innerDataWithHash.AsSpan(1).ToArray());
        }
    }

    private IPQInnerData ParsePqInnerDataOld(byte[] innerDataWithHash)
    {
        var span = innerDataWithHash.AsSpan();
        var shaHash = span[..20];
        var innerData = span[20..];
        ReadOnlyMemory<byte> buffer = innerDataWithHash.AsMemory(20, innerDataWithHash.Length - 20);
        var oldLength = buffer.Length;
        var tPqInnerData = buffer.Read<IPQInnerData>();
        var length = oldLength - buffer.Length;
        var realInnerData = innerData[..length];

        Span<byte> calcHash = stackalloc byte[20];
        SHA1.HashData(realInnerData, calcHash);

        // https://corefork.telegram.org/mtproto/security_guidelines
        // "the first 20 bytes of answer_with_hash must be equal to SHA1 of the remainder" - a mismatch has
        // to reject the handshake. Logging and carrying on would leave the check with no effect at all.
        if (!CryptographicOperations.FixedTimeEquals(shaHash, calcHash))
        {
            logger.LogWarning("PQInnerData SHA1 hash mismatch");

            throw new DhHandshakeRejectedException("PQInnerData SHA1 hash mismatch");
        }

        return tPqInnerData;
    }

    private (byte[] a, byte[] ga) GenerateAAndGa()
    {
        var g = AuthConsts.G.ToBigEndianBigInteger();
        var dhPrime = AuthConsts.DhPrime;
        while (true)
        {
            var aBytes = RandomNumberGenerator.GetBytes(256);
            var a = aBytes.ToBigEndianBigInteger();

            var ga = BigInteger.ModPow(g, a, dhPrime);
            if (IsGoodGaOrGb(ga, dhPrime))
            {
                return (aBytes, ga.ToByteArray(true, true));
            }
        }
    }

    private IPQInnerData ParsePqInnerData(ReadOnlySpan<byte> keyAesEncryptedBytes)
    {
        const int tempKeyLength = 32;
        var tempBytes = ArrayPool<byte>.Shared.Rent(keyAesEncryptedBytes.Length + 32 + 32 + 32);

        try
        {
            var tempSpan = tempBytes.AsSpan(0, keyAesEncryptedBytes.Length + 32 + 32 + 32);
            var startIndex = keyAesEncryptedBytes.Length - tempKeyLength;
            var dataWithHash = tempSpan[..(keyAesEncryptedBytes.Length - tempKeyLength)];

            var aesEncryptedSha256Hash = tempSpan.Slice(startIndex, 32);
            var calculatedHash = tempSpan.Slice(startIndex + 32, 32);
            var aesEncrypted = keyAesEncryptedBytes[tempKeyLength..];

            var tempKeyXor = keyAesEncryptedBytes[..tempKeyLength];
            SHA256.HashData(aesEncrypted, aesEncryptedSha256Hash);
            var tempKey = Xor(tempKeyXor, aesEncryptedSha256Hash);
            Span<byte> tempIv1 = stackalloc byte[32];
            aesHelper.DecryptIge(aesEncrypted, tempKey, tempIv1, dataWithHash);

            var dataPaddingReversed = dataWithHash[..^32];
            var hash = dataWithHash[^32..];
            dataPaddingReversed.Reverse();
            var dataWithPadding = dataPaddingReversed;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(tempKey);
            hasher.AppendData(dataWithPadding);
            hasher.GetHashAndReset(calculatedHash);

            if (!CryptographicOperations.FixedTimeEquals(hash, calculatedHash))
            {
                logger.LogWarning("PQInnerData hash mismatch");

                throw new DhHandshakeRejectedException("PQInnerData hash mismatch");
            }

            // Deserialize strictly from the recovered data_with_padding block. The rented buffer is
            // wider than the plaintext and ArrayPool.Rent does not clear it, so passing the whole array
            // would let an oversized TL length header read leftover bytes from an earlier handshake.
            ReadOnlyMemory<byte> payload = tempBytes.AsMemory(0, dataWithPadding.Length);
            var tPqInnerData = payload.Read<IPQInnerData>();

            return tPqInnerData;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBytes);
        }
    }

    private TServerDHParamsOk SerializeResponse(
        IPQInnerData pqInnerData,
        TServerDHInnerData dhInnerData
    )
    {
        return SerializeResponse(
            pqInnerData.Nonce,
            pqInnerData.NewNonce,
            pqInnerData.ServerNonce,
            dhInnerData
        );
    }

    private TServerDHParamsOk SerializeResponse(
        byte[] nonce,
        byte[] newNonce,
        byte[] serverNonce,
        TServerDHInnerData dhInnerData
    )
    {
        using var writer = new ArrayPoolBufferWriter<byte>();
        dhInnerData.Serialize(writer);

        var writtenCount = writer.WrittenCount;
        var totalLength = writtenCount + 20;// 20=SHA1 hash length
        var tempBytes = ArrayPool<byte>.Shared.Rent(totalLength + 32 + 16);
        var tempSpan = tempBytes.AsSpan();
        try
        {
            var sha1Hash = tempSpan.Slice(0, 20);
            var answerWithHashLength = writtenCount + 20;
            if (answerWithHashLength % 16 != 0)
            {
                answerWithHashLength += 16 - (answerWithHashLength % 16);
            }
            var answerWithHashSpan = tempSpan.Slice(0, answerWithHashLength);
            SHA1.HashData(writer.WrittenSpan, sha1Hash);
            sha1Hash.CopyTo(answerWithHashSpan);
            writer.WrittenSpan.CopyTo(answerWithHashSpan.Slice(20));

            // "the encrypted data is padded with random bytes to a length divisible by 16 immediately prior
            // to encryption" - https://corefork.telegram.org/mtproto/auth_key
            // The buffer comes from ArrayPool and is not zeroed, so without this the alignment padding would
            // be recycled heap contents, encrypted and handed to the client.
            RandomNumberGenerator.Fill(answerWithHashSpan[(20 + writtenCount)..]);

            var aesKey = new byte[32];
            Span<byte> aesIv = stackalloc byte[32];
            mtpHelper.CalcTempAesKeyData(newNonce, serverNonce, aesKey, aesIv);

            aesHelper.EncryptIge(answerWithHashSpan, aesKey, aesIv, answerWithHashSpan);

            return new TServerDHParamsOk
            {
                EncryptedAnswer = answerWithHashSpan.ToArray(),
                Nonce = nonce,
                ServerNonce = serverNonce
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBytes);
        }
    }

    private byte[] Xor(ReadOnlySpan<byte> src, ReadOnlySpan<byte> dest)
    {
        var bytes = new byte[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            bytes[i] = (byte)(src[i] ^ dest[i]);
        }

        return bytes;
    }
}