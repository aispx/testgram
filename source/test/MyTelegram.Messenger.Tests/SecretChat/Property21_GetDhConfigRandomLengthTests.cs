using System.Reflection;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 21: random field length in messages.getDhConfig.
///
/// For any <c>random_length</c> in [1, 256], <c>messages.getDhConfig</c> returns a <c>random</c> field
/// containing exactly <c>random_length</c> bytes; outside that range the request is rejected with
/// RANDOM_LENGTH_INVALID. Independently of the length, the returned Diffie-Hellman parameters are the
/// server's fixed 2048-bit configuration: a client whose cached <c>version</c> differs from the server's
/// current version gets a full <c>messages.dhConfig</c> (g = 3, p = the 256-byte 2048-bit safe prime,
/// version = 2), and a client already on the current version gets <c>messages.dhConfigNotModified</c>
/// carrying only the random bytes. The prime is stable across calls, because clients verify the safe prime
/// once and cache the verdict keyed by version — a p that varied per call would either be re-verified
/// forever or, worse, silently accepted after a single verification.
///
/// Validates: Requirements 2.4 (and the handler's documented RANDOM_LENGTH_INVALID contract).
///
/// How this is tested: <see cref="DhConfigCase"/> generates a <c>random_length</c> drawn from
/// <c>Gen.Choose(1, 256)</c> — with the two extremes (1 and 256) explicitly over-weighted so the closed
/// interval's endpoints are hit on every run — crossed with a client-supplied <c>version</c> drawn from a
/// frequency mix of 0 (a fresh client with no cached config), the server's current version (2), stale
/// versions below it and future versions above it. Every generated case drives the REAL production handler
/// <c>MyTelegram.Messenger.Handlers.LatestLayer.Messages.GetDhConfigHandler</c>; the type is
/// <c>internal sealed</c> and <c>HandleCoreAsync</c> is <c>protected</c>, so both are reached by reflection
/// (no <c>InternalsVisibleTo</c> is added). The expected result shape is derived independently of the
/// handler from the case alone — version == 2 means dhConfigNotModified, anything else means dhConfig —
/// and every field is asserted against a constant fixed by the protocol rather than against a value read
/// back out of the response: the byte count against the generated <c>random_length</c>, g against the
/// literal 3, p against <see cref="AuthConsts.Dh2048P"/> byte-for-byte (and its 256-byte length), and the
/// advertised version against the literal 2. Stability of p is asserted by invoking a freshly constructed
/// handler a second time on the same case and comparing the two primes byte-for-byte. The out-of-range
/// rejections are pinned by an explicit <see cref="Theory"/> over the boundary values 0, -1, 257 and
/// <c>int.MaxValue</c> (and <c>int.MinValue</c>), each attempted with both an up-to-date and a stale
/// version to prove the length check runs before — and independently of — the version branch. The property
/// executes a minimum of 100 generated cases per run.
/// </summary>
public class Property21_GetDhConfigRandomLengthTests
{
    /// <summary>The DH parameter-set version the server currently advertises (GetDhConfigHandler).</summary>
    private const int ServerVersion = 2;

    /// <summary>The generator g of the server's fixed 2048-bit DH configuration.</summary>
    private const int ServerG = 3;

    private const string HandlerTypeName =
        "MyTelegram.Messenger.Handlers.LatestLayer.Messages.GetDhConfigHandler";

    [Property(Arbitrary = new[] { typeof(DhConfigArbitraries) }, MaxTest = 200)]
    public void GetDhConfig_returns_exactly_random_length_bytes_and_the_fixed_dh_parameters(DhConfigCase @case)
    {
        var result = Invoke(@case.ClientVersion, @case.RandomLength);

        if (@case.ClientVersion == ServerVersion)
        {
            // ---- Client already holds the current parameter set: only the random bytes come back ----
            var notModified = result.ShouldBeOfType<TDhConfigNotModified>();
            notModified.Random.Length.ShouldBe(@case.RandomLength);

            return;
        }

        // ---- Any other cached version: the full parameter set is (re)delivered --------------------
        var config = result.ShouldBeOfType<TDhConfig>();

        config.Random.Length.ShouldBe(@case.RandomLength);
        config.G.ShouldBe(ServerG);
        config.Version.ShouldBe(ServerVersion);

        // p is the 2048-bit safe prime, byte-for-byte, and never a truncated or re-generated value.
        config.P.Length.ShouldBe(256);
        config.P.ToArray().ShouldBe(AuthConsts.Dh2048P);

        // p must be identical across calls: clients verify the safe prime once and cache that verdict.
        var second = Invoke(@case.ClientVersion, @case.RandomLength).ShouldBeOfType<TDhConfig>();
        second.P.ToArray().ShouldBe(config.P.ToArray());
        second.G.ShouldBe(config.G);
        second.Version.ShouldBe(config.Version);
    }

    /// <summary>
    /// Lengths outside the closed interval [1, 256] are rejected with RANDOM_LENGTH_INVALID, and the
    /// rejection precedes the version branch — the same error is raised whether or not the client already
    /// holds the current parameter set.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, ServerVersion)]
    [InlineData(-1, 0)]
    [InlineData(-1, ServerVersion)]
    [InlineData(257, 0)]
    [InlineData(257, ServerVersion)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(int.MaxValue, ServerVersion)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MinValue, ServerVersion)]
    public void GetDhConfig_rejects_a_random_length_outside_1_to_256(int randomLength, int version)
    {
        var ex = Should.Throw<RpcException>(() => Invoke(version, randomLength));

        ex.RpcError.ShouldBe(RpcErrors.RpcErrors400.RandomLengthInvalid);
    }

    /// <summary>The two endpoints of the accepted interval are valid and produce exactly that many bytes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public void GetDhConfig_accepts_both_endpoints_of_the_closed_interval(int randomLength)
    {
        Invoke(version: 0, randomLength).ShouldBeOfType<TDhConfig>().Random.Length.ShouldBe(randomLength);
        Invoke(ServerVersion, randomLength).ShouldBeOfType<TDhConfigNotModified>().Random.Length
            .ShouldBe(randomLength);
    }

    // ---- Reflection invocation of the internal handler ------------------------------------------------

    /// <summary>
    /// Constructs the <c>internal sealed</c> GetDhConfigHandler and invokes its
    /// <c>protected HandleCoreAsync(IRequestInput, RequestGetDhConfig)</c> core path via reflection,
    /// unwrapping the reflection wrapper so a thrown <see cref="RpcException"/> surfaces unchanged.
    /// </summary>
    private static IDhConfig Invoke(int version, int randomLength)
    {
        var handlerType = typeof(SecretChatAppService).Assembly.GetType(HandlerTypeName, throwOnError: true)!;

        var handler = Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: Array.Empty<object>(),
            culture: null)!;

        var method = handlerType.GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HandleCoreAsync not found on GetDhConfigHandler.");

        var request = new RequestGetDhConfig { Version = version, RandomLength = randomLength };
        var input = SecretChatTestHarness.Input(SecretChatTestHarness.AdminId,
            SecretChatTestHarness.AdminPermAuthKeyId);

        Task<IDhConfig> task;
        try
        {
            task = (Task<IDhConfig>)method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = task.GetAwaiter().GetResult();
        result.ShouldNotBeNull();

        return result;
    }
}

/// <summary>
/// One generated getDhConfig case: the requested <c>random_length</c> (always inside the accepted closed
/// interval [1, 256]) and the parameter-set <c>version</c> the client claims to have cached.
/// </summary>
public sealed record DhConfigCase(int RandomLength, int ClientVersion);

/// <summary>
/// FsCheck generators for Property 21. Only the case record gets an arbitrary; both of its fields are
/// drawn from explicit <c>Gen</c> combinators so no primitive arbitrary is registered onto itself.
/// </summary>
public static class DhConfigArbitraries
{
    public static Arbitrary<DhConfigCase> Case() => Arb.From(CaseGen);

    /// <summary>
    /// Uniform over the whole accepted interval, with the two endpoints over-weighted so 1 and 256 are
    /// exercised on every run rather than being left to chance in a 256-wide range.
    /// </summary>
    private static Gen<int> RandomLength =>
        Gen.Frequency(
            Tuple.Create(1, Gen.Constant(1)),
            Tuple.Create(1, Gen.Constant(256)),
            Tuple.Create(6, Gen.Choose(1, 256)));

    /// <summary>
    /// The version a client claims to hold: 0 (fresh client), the server's current version (2), a stale
    /// version below it, or a version from the future.
    /// </summary>
    private static Gen<int> ClientVersion =>
        Gen.Frequency(
            Tuple.Create(3, Gen.Constant(0)),
            Tuple.Create(3, Gen.Constant(2)),
            Tuple.Create(2, Gen.Choose(-1000, 1)),
            Tuple.Create(2, Gen.Choose(3, 1000)));

    private static Gen<DhConfigCase> CaseGen =>
        from randomLength in RandomLength
        from clientVersion in ClientVersion
        select new DhConfigCase(randomLength, clientVersion);
}
