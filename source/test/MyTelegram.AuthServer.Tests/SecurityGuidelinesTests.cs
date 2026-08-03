namespace MyTelegram.AuthServer.Tests;

/// <summary>
///     Regression tests for https://corefork.telegram.org/mtproto/security_guidelines as they apply to the
///     server side of the auth key handshake.
/// </summary>
public class SecurityGuidelinesTests
{
    private static readonly BigInteger TwoPow1984 = BigInteger.Pow(2, 2048 - 64);

    public static TheoryData<string, byte[]> RejectedGbValues()
    {
        var p = AuthConsts.DhPrime;

        return new TheoryData<string, byte[]>
        {
            { "zero", ToBytes(BigInteger.Zero) },
            { "one", ToBytes(BigInteger.One) },
            { "two", ToBytes(new BigInteger(2)) },
            { "p-1", ToBytes(p - 1) },
            { "p", ToBytes(p) },
            { "p+1", ToBytes(p + 1) },
            { "just below the 2^1984 safety bound", ToBytes(TwoPow1984 - 1) },
            { "just above the upper safety bound", ToBytes(p - TwoPow1984 + 1) }
        };
    }

    public static TheoryData<string, byte[]> AcceptedGbValues()
    {
        var p = AuthConsts.DhPrime;

        return new TheoryData<string, byte[]>
        {
            { "just inside the lower safety bound", ToBytes(TwoPow1984 + 1) },
            { "just inside the upper safety bound", ToBytes(p - TwoPow1984 - 1) },
            { "midpoint", ToBytes(p / 2) }
        };
    }

    [Theory]
    [MemberData(nameof(RejectedGbValues))]
    public async Task SetClientDhParams_WithOutOfRangeGb_AnswersDhGenFailAndDerivesNoKey(string because, byte[] gb)
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();

        var result = await ctx.Step3.SetClientDhParamsAnswerAsync(ctx.BuildSetClientDhParams(state, gb));

        result.Rejected.ShouldBeTrue(because);
        result.SetClientDhParamsAnswer.ShouldBeOfType<TDhGenFail>(because);
        result.AuthKeyId.ShouldBe(0, because);
        result.AuthKey.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AcceptedGbValues))]
    public async Task SetClientDhParams_WithInRangeGb_AnswersDhGenOk(string because, byte[] gb)
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();

        var result = await ctx.Step3.SetClientDhParamsAnswerAsync(ctx.BuildSetClientDhParams(state, gb));

        result.Rejected.ShouldBeFalse(because);
        result.SetClientDhParamsAnswer.ShouldBeOfType<TDhGenOk>(because);
        result.AuthKey.Length.ShouldBe(256);
        result.AuthKeyId.ShouldNotBe(0);
    }

    [Fact]
    public async Task SetClientDhParams_WithGbLongerThan256Bytes_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();
        var request = ctx.BuildSetClientDhParams(state, RandomNumberGenerator.GetBytes(257));

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(request));
    }

    [Fact]
    public async Task SetClientDhParams_WithCorruptedSha1_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();
        var request = ctx.BuildSetClientDhParams(state, GoodGb(), corruptHash: true);

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(request));
    }

    [Fact]
    public async Task SetClientDhParams_WithNonZeroRetryId_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();
        var request = ctx.BuildSetClientDhParams(state, GoodGb(), retryId: 42);

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(request));
    }

    [Fact]
    public async Task SetClientDhParams_WithOversizedPadding_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();

        // answer_with_hash allows 0-15 padding bytes; anything beyond that is not covered by the hash.
        var request = ctx.BuildSetClientDhParams(state, GoodGb(), extraPadding: 32);

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(request));
    }

    [Fact]
    public async Task HandshakeState_IsSingleUse_SoTheSameSecretExponentCannotBeProbedTwice()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();

        await ctx.Step3.SetClientDhParamsAnswerAsync(ctx.BuildSetClientDhParams(state, GoodGb()));

        // The second attempt must not find the cached secret exponent `a` any more.
        await Should.ThrowAsync<InvalidOperationException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(ctx.BuildSetClientDhParams(state, GoodGb())));
    }

    [Fact]
    public async Task HandshakeState_IsDroppedEvenWhenTheHandshakeIsRejected()
    {
        var ctx = new HandshakeTestContext();
        var state = ctx.SeedPostStep2State();

        await ctx.Step3.SetClientDhParamsAnswerAsync(
            ctx.BuildSetClientDhParams(state, ToBytes(BigInteger.One)));

        await Should.ThrowAsync<InvalidOperationException>(
            () => ctx.Step3.SetClientDhParamsAnswerAsync(ctx.BuildSetClientDhParams(state, GoodGb())));
    }

    [Fact]
    public async Task ReqDhParams_WithWrongPublicKeyFingerprint_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var (nonce, serverNonce, newNonce) = await SeedStep1Async(ctx);

        var request = ctx.BuildReqDhParams(nonce, serverNonce, newNonce);
        request.PublicKeyFingerprint ^= 1;

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step2.GetServerDhParamsAsync(request));
    }

    [Fact]
    public async Task ReqDhParams_WithTamperedEncryptedData_IsRejected()
    {
        var ctx = new HandshakeTestContext();
        var (nonce, serverNonce, newNonce) = await SeedStep1Async(ctx);

        var request = ctx.BuildReqDhParams(nonce, serverNonce, newNonce);
        request.EncryptedData[100] ^= 0xff;

        await Should.ThrowAsync<DhHandshakeRejectedException>(
            () => ctx.Step2.GetServerDhParamsAsync(request));
    }

    [Fact]
    public async Task ServerDhInnerData_IsPaddedWithRandomBytes_NotRecycledHeap()
    {
        // The padding of answer_with_hash used to be whatever the pooled buffer happened to contain, which
        // both leaked server heap to the client and made the padding non-random.
        var paddings = new List<string>();

        for (var i = 0; i < 8; i++)
        {
            var ctx = new HandshakeTestContext();
            var (nonce, serverNonce, newNonce) = await SeedStep1Async(ctx);

            var answer = await ctx.Step2.GetServerDhParamsAsync(ctx.BuildReqDhParams(nonce, serverNonce, newNonce));
            var ok = answer.ServerDhParams.ShouldBeOfType<TServerDHParamsOk>();
            var decrypted = ctx.DecryptServerDhAnswer(ok.EncryptedAnswer, newNonce, serverNonce);

            ReadOnlyMemory<byte> buffer = decrypted.AsMemory(20);
            var remaining = buffer.Length;
            buffer.Read<TServerDHInnerData>();
            var consumed = remaining - buffer.Length;

            // The hash must cover exactly the object, and only alignment padding may follow it.
            SHA1.HashData(decrypted.AsSpan(20, consumed))
                .ShouldBe(decrypted[..20], "the first 20 bytes must be SHA1 of the answer");

            var padding = decrypted.AsSpan(20 + consumed).ToArray();
            padding.Length.ShouldBeInRange(0, 15);
            padding.Length.ShouldBeGreaterThan(0, "this fixture is only meaningful when padding exists");

            paddings.Add(Convert.ToHexString(padding));
        }

        paddings.Distinct().Count().ShouldBeGreaterThan(1, "padding must be random, not recycled buffer contents");
    }

    [Fact]
    public async Task ReqDhParams_WithInnerDataDeclaringMoreBytesThanTheRsaBlock_IsRejected()
    {
        // RSA_PAD recovers exactly 192 bytes of data_with_padding. A TL `bytes` header that declares more
        // than that must be refused: deserializing from the whole pooled buffer instead of the recovered
        // block would let the extra length read another handshake's leftovers out of ArrayPool.
        var ctx = new HandshakeTestContext();
        var (nonce, serverNonce, _) = await SeedStep1Async(ctx);

        // p_q_inner_data#83c95aec, little-endian, followed by a long-form TL `bytes` header whose length
        // (0x010000 = 65536) is far past the 192-byte block.
        byte[] rawInnerData = [0xec, 0x5a, 0xc9, 0x83, 0xfe, 0x00, 0x00, 0x01];

        var request = ctx.BuildReqDhParamsWithRawInnerData(nonce, serverNonce, rawInnerData);

        await Should.ThrowAsync<Exception>(() => ctx.Step2.GetServerDhParamsAsync(request));
    }

    private static async Task<(byte[] Nonce, byte[] ServerNonce, byte[] NewNonce)> SeedStep1Async(
        HandshakeTestContext ctx)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var serverNonce = RandomNumberGenerator.GetBytes(16);
        var newNonce = RandomNumberGenerator.GetBytes(32);

        await ctx.Cache.SetAsync(
            AuthCacheItem.GetCacheKey(serverNonce),
            new AuthCacheItem(nonce, serverNonce, AuthConsts.P, AuthConsts.Q, false));

        return (nonce, serverNonce, newNonce);
    }

    private static byte[] GoodGb()
    {
        var b = RandomNumberGenerator.GetBytes(256).ToBigEndianBigInteger();
        return ToBytes(BigInteger.ModPow(AuthConsts.G3, b, AuthConsts.DhPrime));
    }

    private static byte[] ToBytes(BigInteger value)
    {
        return value.IsZero ? [0] : value.ToByteArray(true, true);
    }
}
