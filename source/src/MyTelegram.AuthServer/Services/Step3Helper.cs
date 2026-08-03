namespace MyTelegram.AuthServer.Services;

public class Step3Helper(
    IAesHelper aesHelper,
    IHashHelper hashHelper,
    IMtpHelper mtpHelper,
    ILogger<Step3Helper> logger,
    IAuthKeyIdHelper authKeyIdHelper,
    ICacheManager<AuthCacheItem> cacheManager
) : Step1To3Helper, IStep3Helper, ISingletonDependency
{
    /// <summary>g_b is a 2048-bit value, so it can never need more than 256 bytes on the wire.</summary>
    private const int MaxDhValueLength = 256;

    public async Task<Step3Output> SetClientDhParamsAnswerAsync(RequestSetClientDHParams req)
    {
        var cacheKey = GetAuthCacheKey(req.ServerNonce);
        var cachedAuthKey = await cacheManager.GetAsync(cacheKey);
        if (cachedAuthKey?.A == null)
        {
            throw new InvalidOperationException(
                $"Cannot find cached auth key info, nonce: {req.Nonce.ToHexString()}"
            );
        }

        if (cachedAuthKey.NewNonce == null)
        {
            throw new ArgumentNullException(nameof(cachedAuthKey.NewNonce));
        }

        // The handshake state (which holds the server's secret exponent `a`) is single-use: drop it before
        // doing anything else so that the same `a` can never be probed with a second, different g_b.
        await cacheManager.RemoveAsync(cacheKey);

        CheckRequestData(cachedAuthKey.Nonce, req.Nonce, "Nonce");
        CheckRequestData(cachedAuthKey.ServerNonce, req.ServerNonce, "ServerNonce");

        var aesKey = new byte[32];
        Span<byte> aesIv = stackalloc byte[32];
        mtpHelper.CalcTempAesKeyData(
           cachedAuthKey.NewNonce,
           cachedAuthKey.ServerNonce,
           aesKey,
           aesIv
       );
        var dhInnerData = DeserializeRequest(req, aesKey, aesIv);

        CheckRequestData(cachedAuthKey.Nonce, dhInnerData.Nonce, "Nonce");
        CheckRequestData(cachedAuthKey.ServerNonce, dhInnerData.ServerNonce, "ServerNonce");

        // Because the handshake state is single-use there is never a live previous attempt to retry against,
        // so retry_id must always be 0. See https://corefork.telegram.org/mtproto/auth_key
        if (dhInnerData.RetryId != 0)
        {
            throw new DhHandshakeRejectedException(
                $"Unexpected retry_id {dhInnerData.RetryId}, expected 0."
            );
        }

        var a = cachedAuthKey.A;
        var gb = dhInnerData.GB;

        // https://corefork.telegram.org/mtproto/security_guidelines#g-a-and-g-b-validation
        // Both sides must check that g_b is greater than 1 and less than dh_prime - 1, and (recommended)
        // that it lies within [2^{2048-64}, dh_prime - 2^{2048-64}]. Without this a client can send
        // g_b in {0, 1, dh_prime - 1} and pin the resulting auth key to a publicly known constant.
        if (gb == null || gb.Length == 0 || gb.Length > MaxDhValueLength)
        {
            throw new DhHandshakeRejectedException(
                $"Invalid g_b length: {gb?.Length.ToString() ?? "null"}."
            );
        }

        var gbValue = gb.ToBigEndianBigInteger();
        var authKeyBytes = BigInteger
            .ModPow(gbValue, a.ToBigEndianBigInteger(), AuthConsts.DhPrime)
            .ToByteArray(true, true)
            .ToBytes256();

        if (!IsGoodGaOrGb(gbValue, AuthConsts.DhPrime))
        {
            logger.LogWarning("Rejecting handshake: g_b is out of the allowed range.");

            // The client still gets an authenticated failure (new_nonce_hash3 is bound to the candidate key),
            // but the key is never registered.
            return new Step3Output(
                0,
                [],
                0,
                cachedAuthKey.IsPermanent,
                CreateDhGenFailAnswer(req, cachedAuthKey.NewNonce, authKeyBytes),
                cachedAuthKey.DcId,
                true
            );
        }

        var dto = new Step3Output(
            authKeyIdHelper.GetAuthKeyId(authKeyBytes),
            authKeyBytes,
            mtpHelper.ComputeSalt(cachedAuthKey.NewNonce, dhInnerData.ServerNonce),
            cachedAuthKey.IsPermanent,
            CreateDhGenOkAnswer(req, cachedAuthKey.NewNonce, authKeyBytes),
            cachedAuthKey.DcId
        );

        return dto;
    }

    private TClientDHInnerData DeserializeRequest(
        RequestSetClientDHParams serverDhParams,
        byte[] key,
        ReadOnlySpan<byte> iv
    )
    {
        var tempBytes = ArrayPool<byte>.Shared.Rent(serverDhParams.EncryptedData.Length + 20);
        var tempSpan = tempBytes.AsSpan(0, serverDhParams.EncryptedData.Length + 20);
        var answerWithHash = tempSpan.Slice(0, serverDhParams.EncryptedData.Length);
        try
        {
            aesHelper.DecryptIge(
                serverDhParams.EncryptedData,
                key,
                iv,
                answerWithHash
            );

            var hash = answerWithHash[..20];
            var answer = answerWithHash[20..];
            ReadOnlyMemory<byte> buffer = tempBytes.AsMemory(20, answerWithHash.Length - 20);
            var oldLength = buffer.Length;
            var obj = buffer.Read<TClientDHInnerData>();
            var consumed = oldLength-buffer.Length;
            var paddingCount = (int)(answer.Length - consumed);

            // answer_with_hash := SHA1(answer) + answer + (0-15 random bytes)
            if (paddingCount is < 0 or > 15)
            {
                throw new DhHandshakeRejectedException(
                    $"Invalid answer_with_hash padding length: {paddingCount}, expected 0-15."
                );
            }

            var data = answer[..^paddingCount];
            var calcHash = tempSpan[^20..];
            SHA1.HashData(data, calcHash);
            if (!CryptographicOperations.FixedTimeEquals(hash, calcHash))
            {
                logger.LogWarning("Answer sha1 hash mismatch.");

                throw new DhHandshakeRejectedException("Answer sha1 hash mismatch.");
            }

            return obj;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempBytes);
        }
    }

    private TDhGenOk CreateDhGenOkAnswer(
        RequestSetClientDHParams req,
        byte[] newNonce,
        byte[] authKey
    )
    {
        var newNonceHash1 = CreateNewNonceHash(newNonce, authKey, 1);

        return new TDhGenOk
        {
            Nonce = req.Nonce,
            ServerNonce = req.ServerNonce,
            NewNonceHash1 = newNonceHash1
        };
    }

    // dh_gen_retry is deliberately not produced: the handshake state is single-use (the cache entry is
    // dropped at the top of SetClientDhParamsAnswerAsync), so there is never a live attempt for the client
    // to retry against. A client that wants another go restarts from req_pq.
    private TDhGenFail CreateDhGenFailAnswer(
        RequestSetClientDHParams req,
        byte[] newNonce,
        byte[] authKey
    )
    {
        var newNonceHash3 = CreateNewNonceHash(newNonce, authKey, 3);

        return new TDhGenFail
        {
            Nonce = req.Nonce,
            ServerNonce = req.ServerNonce,
            NewNonceHash3 = newNonceHash3
        };
    }

    private byte[] CreateNewNonceHash(byte[] newNonce, byte[] authKey, byte n)
    {
        // https://core.telegram.org/mtproto/auth_key#9-server-responds-in-one-of-three-ways
        // new_nonce_hash1, new_nonce_hash2, and new_nonce_hash3 are obtained as the 128 lower - order bits of SHA1 of
        // the byte string derived from the new_nonce string by adding a single byte with the value of 1, 2, or 3, and followed
        // by another 8 bytes with auth_key_aux_hash.Different values are required to prevent an intruder from changing server
        // response dh_gen_ok into dh_gen_retry.

        var authKeyAuxHash = SHA1.HashData(authKey).AsSpan(0, 8);
        Span<byte> newNonceWithAuxHashBytes = stackalloc byte[newNonce.Length + 1 + 8];
        newNonce.CopyTo(newNonceWithAuxHashBytes);
        newNonceWithAuxHashBytes[newNonce.Length] = n;
        authKeyAuxHash.CopyTo(newNonceWithAuxHashBytes[(newNonce.Length + 1)..]);
        var newNonceHashN = hashHelper.Sha1(newNonceWithAuxHashBytes);

        return newNonceHashN.AsSpan(4).ToArray();
    }
}
