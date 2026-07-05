using System.Reflection;
using CsCheck;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 7 (Per-user access-hash authorization).
///
/// Property 7: For any call-referencing request, access is granted <b>iff</b> the supplied
/// <c>access_hash</c> equals the value issued to the <i>requesting</i> user for that call; every
/// mismatch (including supplying the <i>other</i> party's issued hash) yields the invalid-peer error
/// (<c>CALL_PEER_INVALID</c> for 1:1 calls).
///
/// <para>The property drives the real <c>DiscardCallHandler</c> (a representative 1:1 call-referencing
/// method that both the Caller and the Callee may invoke) over an in-memory Mongo store. Distinct
/// per-user access hashes are issued to the caller and callee (R29.3) - each equal to the value the
/// access-hash helper would generate for that user - so the document's per-user check and the helper
/// fallback authorize exactly the same value. For every randomly generated (requesting party, supplied
/// hash) pair it asserts:</para>
/// <list type="bullet">
///   <item>when the supplied hash equals the requesting user's issued hash, the request is authorized
///     (the call is discarded and a <c>phoneCallDiscarded</c> for the session is returned);</item>
///   <item>otherwise - a wrong hash, a zero hash, or the OTHER party's issued hash - the request is
///     rejected with <c>CALL_PEER_INVALID</c> and the session is left untouched.</item>
/// </list>
///
/// **Validates: Requirements 29.1, 29.2, 29.3**
/// </summary>
public class AccessHashAuthorizationPropertyTests
{
    private const long CallId = 4242;
    private const long CallerId = 1;
    private const long CalleeId = 2;

    /// <summary>How the supplied access_hash relates to the value issued to the requesting user.</summary>
    private enum SuppliedHashKind
    {
        /// <summary>Exactly the hash issued to the requesting user - the only value that should authorize.</summary>
        Correct = 0,
        /// <summary>The hash issued to the OTHER participant - must be rejected (R29.3 independence).</summary>
        OtherParty = 1,
        /// <summary>A zero hash - never valid.</summary>
        Zero = 2,
        /// <summary>An arbitrary non-zero hash.</summary>
        Arbitrary = 3
    }

    [Fact]
    public void DiscardCall_IsAuthorized_IffSuppliedHashEqualsTheRequestingUsersIssuedHash()
    {
        // Either participant may reference the call; the session may be in any pre-discard state; the
        // supplied access_hash is varied across "correct", "other party", "zero", and arbitrary values.
        var gen =
            from requestingIsCaller in Gen.Bool
            from initialState in Gen.OneOfConst("requested", "waiting", "received", "accepted", "confirmed")
            from kind in Gen.Int[0, 3]
            from arbitrary in Gen.Long
            select new AuthScenario(requestingIsCaller, initialState, (SuppliedHashKind)kind, arbitrary);

        gen.Sample(scenario => RunScenario(scenario), iter: 300);
    }

    private static void RunScenario(AuthScenario scenario)
    {
        var accessHash = new FakeAccessHashHelper2();

        // R29.3: issue distinct per-user access hashes for the caller and callee. Each equals the value
        // the helper would generate for that user, so the document's strict per-user check and the
        // helper fallback authorize exactly the same value - isolating the "per-user" property.
        var issuedCaller = accessHash.GenerateAccessHash(
            CallerId, PhoneTestFixtures.DefaultAccessHashKeyId(CallerId), CallId, AccessHashType.Call);
        var issuedCallee = accessHash.GenerateAccessHash(
            CalleeId, PhoneTestFixtures.DefaultAccessHashKeyId(CalleeId), CallId, AccessHashType.Call);
        issuedCaller.ShouldNotBe(issuedCallee, "the caller and callee must receive distinct per-user access hashes");

        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var collection = database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
        collection.InsertOne(new CallSessionDocument
        {
            Id = CallId,
            CallId = CallId,
            CallerId = CallerId,
            CalleeId = CalleeId,
            CallerAccessHash = issuedCaller,
            CalleeAccessHash = issuedCallee,
            State = scenario.InitialState
        });

        var requestingUserId = scenario.RequestingIsCaller ? CallerId : CalleeId;
        var issuedToRequesting = scenario.RequestingIsCaller ? issuedCaller : issuedCallee;
        var issuedToOther = scenario.RequestingIsCaller ? issuedCallee : issuedCaller;

        var suppliedHash = scenario.Kind switch
        {
            SuppliedHashKind.Correct => issuedToRequesting,
            SuppliedHashKind.OtherParty => issuedToOther,
            SuppliedHashKind.Zero => 0L,
            _ => scenario.Arbitrary
        };

        // The authorization decision must depend ONLY on whether the supplied hash equals the hash
        // issued to the requesting user - regardless of how that value was produced.
        var expectedGranted = suppliedHash != 0 && suppliedHash == issuedToRequesting;

        var sender = new CapturingObjectMessageSender();
        var messageApp = new FakeMessageAppService();
        var userConverter = CreateUserConverter();
        var input = PhoneTestFixtures.RequestInput(requestingUserId).Build();

        var request = new RequestDiscardCall
        {
            Peer = new TInputPhoneCall { Id = CallId, AccessHash = suppliedHash },
            Duration = 0,
            Reason = new TPhoneCallDiscardReasonHangup(),
            Video = false,
            ConnectionId = 0
        };

        if (expectedGranted)
        {
            // R29.1: a matching per-user access_hash authorizes the reference - the call is discarded.
            var updates = InvokeDiscardAsync(database, userConverter, sender, messageApp, accessHash, input, request)
                .GetAwaiter().GetResult();

            var tUpdates = updates.ShouldBeOfType<TUpdates>();
            var discarded = tUpdates.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem()
                .PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>();
            discarded.Id.ShouldBe(CallId);

            store.Documents(PhoneTestFixtures.CallSessionsCollectionName).Single()["State"].AsString
                .ShouldBe("discarded", "an authorized discard must transition the session to discarded");
        }
        else
        {
            // R29.2: any mismatch - including the OTHER party's issued hash - yields CALL_PEER_INVALID.
            var ex = Should.Throw<RpcException>(() =>
                InvokeDiscardAsync(database, userConverter, sender, messageApp, accessHash, input, request)
                    .GetAwaiter().GetResult());
            ex.Message.ShouldBe("CALL_PEER_INVALID");

            // The rejected request must not mutate the session or emit any updates / service messages.
            store.Documents(PhoneTestFixtures.CallSessionsCollectionName).Single()["State"].AsString
                .ShouldBe(scenario.InitialState, "a rejected request must not mutate recorded state");
            sender.Pushes.ShouldBeEmpty("a rejected request must not push any updates");
            messageApp.SentMessages.ShouldBeEmpty("a rejected request must not emit a service message");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IUserConverterService CreateUserConverter()
    {
        var mock = new Mock<IUserConverterService>();
        mock.Setup(x => x.GetUserListAsync(
                It.IsAny<IRequestWithAccessHashKeyId>(),
                It.IsAny<List<long>>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .ReturnsAsync(new List<ILayeredUser>());
        return mock.Object;
    }

    private static async Task<IUpdates> InvokeDiscardAsync(
        IMongoDatabase database,
        IUserConverterService userConverter,
        IObjectMessageSender sender,
        object messageApp,
        object accessHash,
        IRequestInput input,
        RequestDiscardCall request)
    {
        var assembly = typeof(CallSessionDocument).Assembly;
        var type = assembly.GetType("MyTelegram.Messenger.Handlers.LatestLayer.Phone.DiscardCallHandler", throwOnError: true)!;
        var handler = Activator.CreateInstance(type, database, userConverter, sender, messageApp, accessHash)!;
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = await (Task<IObject>)taskObj;
        var rpcResult = (TRpcResult)result;
        return (IUpdates)rpcResult.Result;
    }

    private sealed record AuthScenario(
        bool RequestingIsCaller,
        string InitialState,
        SuppliedHashKind Kind,
        long Arbitrary);
}
