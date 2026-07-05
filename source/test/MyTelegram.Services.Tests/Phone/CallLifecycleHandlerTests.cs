using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Handler-level (example) tests for the 1:1 call lifecycle and the request-time rejections.
///
/// <para>The happy-path test drives the real handlers through the full lifecycle
/// <c>request → received → accept → confirm → discard</c> against an in-memory Mongo store, asserting after
/// every step: the persisted <see cref="CallSessionDocument.State"/> transition, the TL constructor returned
/// synchronously to the caller/callee, and the <c>update*</c> constructor(s) pushed to the other party's
/// active sessions (and, on accept, the <c>phoneCallDiscarded</c> fanned out to the callee's other devices,
/// excluding the accepting device).</para>
///
/// <para>The rejection tests cover the busy/occupy guard (a callee already engaged in a
/// <c>received</c>/<c>accepted</c>/<c>confirmed</c> call rejects new incoming calls with
/// <c>CALL_OCCUPY_FAILED</c>) and the duplicate <c>random_id</c> guard (<c>RANDOM_ID_DUPLICATE</c>).</para>
///
/// Covers Requirements 2.6 (CALL_OCCUPY_FAILED), 2.7 (RANDOM_ID_DUPLICATE) and 6.2 (busy semantics),
/// and exercises the transitions / update-dispatch behaviour of Requirements 2.1-2.2, 4.1-4.3, 5.1-5.2,
/// 6.1 and 7.1-7.2.
/// </summary>
public class CallLifecycleHandlerTests
{
    private const long CallerId = 1;
    private const long CalleeId = 2;
    private const long OtherCallerId = 3;

    // ---- full lifecycle --------------------------------------------------------------------------

    [Fact]
    public async Task FullLifecycle_RequestReceivedAcceptConfirmDiscard_TransitionsAndDispatchesUpdates()
    {
        var harness = new CallHarness();
        var collection = harness.Sessions;

        // -- request (caller) → state 'requested' ---------------------------------------------------
        var response = await harness.RequestCallAsync();

        // R2.1: the caller synchronously receives a phone.phoneCall wrapping phoneCallWaiting.
        PhoneCallOf(response).ShouldBeOfType<TPhoneCallWaiting>();

        var session = await FirstSessionAsync(collection);
        session.State.ShouldBe("requested");

        // R2.2: the callee's active sessions receive updatePhoneCall{ phoneCallRequested }; the caller
        // receives no push.
        var requestedPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(requestedPush).ShouldBeOfType<TPhoneCallRequested>();
        harness.Sender.PushesToUser(CallerId).ShouldBeEmpty();

        // -- received (callee device rings) → state 'received' --------------------------------------
        harness.Sender.Clear();
        var receivedResult = await harness.ReceivedCallAsync();
        receivedResult.ShouldBeOfType<TBoolTrue>();

        (await FirstSessionAsync(collection)).State.ShouldBe("received");

        // R6.1: the caller learns the callee is ringing via updatePhoneCall{ phoneCallWaiting }.
        var ringingPush = harness.Sender.PushesToUser(CallerId).ShouldHaveSingleItem();
        PhoneCallOf(ringingPush).ShouldBeOfType<TPhoneCallWaiting>();

        // -- accept (callee, g_b) → state 'accepted' ------------------------------------------------
        harness.Sender.Clear();
        var acceptResponse = await harness.AcceptCallAsync();

        // R4.1: the accepting callee synchronously receives phone.phoneCall{ phoneCallWaiting }.
        PhoneCallOf(acceptResponse).ShouldBeOfType<TPhoneCallWaiting>();
        (await FirstSessionAsync(collection)).State.ShouldBe("accepted");

        // R4.2: the caller receives updatePhoneCall{ phoneCallAccepted } carrying g_b.
        var acceptedPush = harness.Sender.PushesToUser(CallerId).ShouldHaveSingleItem();
        var accepted = PhoneCallOf(acceptedPush).ShouldBeOfType<TPhoneCallAccepted>();
        accepted.GB.ShouldBe(harness.GbBytes);

        // R4.3: the accepting callee's OTHER devices receive updatePhoneCall{ phoneCallDiscarded },
        // and that push excludes the accepting device (excludeAuthKeyId).
        var otherDevicePush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(otherDevicePush).ShouldBeOfType<TPhoneCallDiscarded>();
        otherDevicePush.ExcludeAuthKeyId.ShouldBe(harness.CalleeInput.AuthKeyId);

        // -- confirm (caller, g_a) → state 'confirmed' ----------------------------------------------
        harness.Sender.Clear();
        var confirmResponse = await harness.ConfirmCallAsync();

        // R5.1: the caller synchronously receives phone.phoneCall{ phoneCall } with connections.
        var confirmedCall = PhoneCallOf(confirmResponse).ShouldBeOfType<MyTelegram.Schema.TPhoneCall>();
        confirmedCall.Connections.Count.ShouldBeGreaterThan(0);
        (await FirstSessionAsync(collection)).State.ShouldBe("confirmed");

        // R5.2: the callee receives updatePhoneCall{ phoneCall } with connection info.
        var confirmPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        var pushedCall = PhoneCallOf(confirmPush).ShouldBeOfType<MyTelegram.Schema.TPhoneCall>();
        pushedCall.Connections.Count.ShouldBeGreaterThan(0);

        // -- discard (caller) → state 'discarded' ---------------------------------------------------
        harness.Sender.Clear();
        var discardResult = await harness.DiscardCallAsync(harness.CallerInput, harness.CallerPeer());

        // R7.1: an Updates with updatePhoneCall{ phoneCallDiscarded } is returned.
        var discardUpdates = discardResult.ShouldBeOfType<TUpdates>();
        var returnedUpdate = discardUpdates.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        returnedUpdate.PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>();
        (await FirstSessionAsync(collection)).State.ShouldBe("discarded");

        // R7.2: the other party (callee) receives updatePhoneCall{ phoneCallDiscarded }.
        var discardPush = harness.Sender.PushesToUser(CalleeId).ShouldHaveSingleItem();
        PhoneCallOf(discardPush).ShouldBeOfType<TPhoneCallDiscarded>();
    }

    // ---- busy / occupy rejection (R2.6, R6.2) ----------------------------------------------------

    [Theory]
    [InlineData("received")]
    [InlineData("accepted")]
    [InlineData("confirmed")]
    public async Task RequestCall_WhenCalleeIsBusy_ThrowsCallOccupyFailed(string busyState)
    {
        var harness = new CallHarness();

        // Establish an existing call to the callee and drive it into a busy state.
        await harness.RequestCallAsync();
        await harness.ReceivedCallAsync();
        if (busyState is "accepted" or "confirmed")
        {
            await harness.AcceptCallAsync();
        }

        if (busyState == "confirmed")
        {
            await harness.ConfirmCallAsync();
        }

        (await FirstSessionAsync(harness.Sessions)).State.ShouldBe(busyState);

        // R2.6 / R6.2: a new incoming call to the same (busy) callee is rejected.
        var otherCaller = PhoneTestFixtures.RequestInput(OtherCallerId).Build();
        var ex = await Should.ThrowAsync<RpcException>(() =>
            harness.RequestCallAsync(otherCaller, CalleeId, randomId: 999_001));
        ex.Message.ShouldBe("CALL_OCCUPY_FAILED");
    }

    [Fact]
    public async Task RequestCall_WhenCalleeOnlyHasWaitingCall_IsNotBusy()
    {
        var harness = new CallHarness();

        // The first call is only in 'requested' (not received/accepted/confirmed), so the callee is
        // not yet "busy": R6.2 only guards received/accepted/confirmed states.
        await harness.RequestCallAsync();
        (await FirstSessionAsync(harness.Sessions)).State.ShouldBe("requested");

        var otherCaller = PhoneTestFixtures.RequestInput(OtherCallerId).Build();
        await harness.RequestCallAsync(otherCaller, CalleeId, randomId: 999_002);

        // Two independent sessions now exist to the same callee.
        var count = await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty);
        count.ShouldBe(2);
    }

    // ---- duplicate random_id rejection (R2.7) ----------------------------------------------------

    [Fact]
    public async Task RequestCall_WithDuplicateRandomId_ThrowsRandomIdDuplicate()
    {
        var harness = new CallHarness();

        await harness.RequestCallAsync(randomId: 424_242);

        // R2.7: the same caller re-using an existing random_id (even for a different callee) is rejected.
        var ex = await Should.ThrowAsync<RpcException>(() =>
            harness.RequestCallAsync(harness.CallerInput, calleeId: 42, randomId: 424_242));
        ex.Message.ShouldBe("RANDOM_ID_DUPLICATE");
    }

    [Fact]
    public async Task RequestCall_DifferentCallersReuseSameRandomId_IsAllowed()
    {
        var harness = new CallHarness();

        // random_id uniqueness is scoped per caller: a different caller may reuse the same value.
        await harness.RequestCallAsync(randomId: 555);
        var otherCaller = PhoneTestFixtures.RequestInput(OtherCallerId).Build();
        await harness.RequestCallAsync(otherCaller, calleeId: 42, randomId: 555);

        var count = await harness.Sessions.CountDocumentsAsync(Builders<CallSessionDocument>.Filter.Empty);
        count.ShouldBe(2);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static MyTelegram.Schema.IPhoneCall PhoneCallOf(IObject response)
    {
        return response.ShouldBeOfType<MyTelegram.Schema.Phone.TPhoneCall>().PhoneCall;
    }

    private static MyTelegram.Schema.IPhoneCall PhoneCallOf(CapturedPush push)
    {
        var update = push.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        return update.PhoneCall;
    }

    private static async Task<CallSessionDocument> FirstSessionAsync(IMongoCollection<CallSessionDocument> collection)
    {
        var session = await collection.Find(Builders<CallSessionDocument>.Filter.Empty).FirstOrDefaultAsync();
        session.ShouldNotBeNull();
        return session!;
    }

    /// <summary>
    /// Builds the real 1:1 call handlers over a shared in-memory Mongo store and drives them through
    /// the lifecycle. The primary call is always CallerId → CalleeId; the primary call's ids / access
    /// hashes are resolved from the persisted session so the peers can address the call.
    /// </summary>
    private sealed class CallHarness
    {
        private readonly object _requestHandler;
        private readonly object _receivedHandler;
        private readonly object _acceptHandler;
        private readonly object _confirmHandler;
        private readonly object _discardHandler;

        public CallHarness()
        {
            Database = PhoneTestFixtures.CreateDatabase(out _);
            Sessions = Database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
            Sender = new CapturingObjectMessageSender();

            var messageAppService = new FakeMessageAppService();
            var accessHashKeyCache = new FakeUserAccessHashKeyCache();
            var accessHashHelper = new FakeAccessHashHelper2();

            var userConverter = new Mock<IUserConverterService>();
            userConverter
                .Setup(x => x.GetUserListAsync(
                    It.IsAny<IRequestWithAccessHashKeyId>(),
                    It.IsAny<List<long>>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>(),
                    It.IsAny<int>()))
                .ReturnsAsync(new List<ILayeredUser>());

            // Privacy allows the call (callback never fired) and P2P is permitted; the callee has not
            // blocked the caller. None of this affects the lifecycle transitions under test.
            var privacy = new Mock<IPrivacyAppService>();
            privacy
                .Setup(x => x.ApplyPrivacyAsync(
                    It.IsAny<long>(),
                    It.IsAny<long>(),
                    It.IsAny<Action<PrivacyValueType>>(),
                    It.IsAny<PrivacyType>()))
                .Returns(Task.CompletedTask);

            var block = new Mock<IBlockCacheAppService>();
            block.Setup(x => x.IsBlockedAsync(It.IsAny<long>(), It.IsAny<long>())).ReturnsAsync(false);

            var options = Options.Create(new MyTelegramMessengerServerOptions
            {
                WebRtcConnections =
                [
                    new WebRtcConnection
                    {
                        Ip = "1.2.3.4",
                        Ipv6 = "",
                        Port = 3478,
                        Turn = true,
                        Stun = false,
                        UserName = "user",
                        Password = "pass"
                    }
                ]
            });

            _requestHandler = CreateHandler("RequestCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashKeyCache, accessHashHelper, block.Object, privacy.Object);
            _receivedHandler = CreateHandler("ReceivedCallHandler",
                Database, userConverter.Object, Sender, accessHashHelper);
            _acceptHandler = CreateHandler("AcceptCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashHelper);
            _confirmHandler = CreateHandler("ConfirmCallHandler",
                Database, userConverter.Object, Sender, options, accessHashHelper, privacy.Object);
            _discardHandler = CreateHandler("DiscardCallHandler",
                Database, userConverter.Object, Sender, messageAppService, accessHashHelper);

            CallerInput = PhoneTestFixtures.RequestInput(CallerId).Build();
            CalleeInput = PhoneTestFixtures.RequestInput(CalleeId).Build();

            GaBytes = ValidDhValue(offset: 0);
            GbBytes = ValidDhValue(offset: 7);
            GaHash = SHA256.HashData(GaBytes);
        }

        public IMongoDatabase Database { get; }
        public IMongoCollection<CallSessionDocument> Sessions { get; }
        public CapturingObjectMessageSender Sender { get; }
        public IRequestInput CallerInput { get; }
        public IRequestInput CalleeInput { get; }
        public byte[] GaBytes { get; }
        public byte[] GbBytes { get; }
        public byte[] GaHash { get; }

        private static IPhoneCallProtocol Protocol() => new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = 65,
            MaxLayer = 92,
            LibraryVersions = new TVector<string> { "3.0.0" }
        };

        public IInputPhoneCall CallerPeer()
        {
            var session = Sessions.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, CallerId)).First();
            return new TInputPhoneCall { Id = session.CallId, AccessHash = session.GetAccessHashForUser(CallerId) };
        }

        public IInputPhoneCall CalleePeer()
        {
            var session = Sessions.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, CallerId)).First();
            return new TInputPhoneCall { Id = session.CallId, AccessHash = session.GetAccessHashForUser(CalleeId) };
        }

        public Task<IObject> RequestCallAsync(int randomId = 100_001)
            => RequestCallAsync(CallerInput, CalleeId, randomId);

        public async Task<IObject> RequestCallAsync(IRequestInput caller, long calleeId, int randomId)
        {
            var request = new RequestRequestCall
            {
                UserId = new TInputUser { UserId = calleeId, AccessHash = 0 },
                RandomId = randomId,
                GAHash = GaHash,
                Protocol = Protocol(),
                Video = false
            };
            return await InvokeAsync(_requestHandler, caller, request);
        }

        public Task<IObject> ReceivedCallAsync()
            => InvokeAsync(_receivedHandler, CalleeInput, new RequestReceivedCall { Peer = CalleePeer() });

        public Task<IObject> AcceptCallAsync()
            => InvokeAsync(_acceptHandler, CalleeInput,
                new RequestAcceptCall { Peer = CalleePeer(), GB = GbBytes, Protocol = Protocol() });

        public Task<IObject> ConfirmCallAsync()
            => InvokeAsync(_confirmHandler, CallerInput,
                new RequestConfirmCall { Peer = CallerPeer(), GA = GaBytes, KeyFingerprint = 987654321L, Protocol = Protocol() });

        public Task<IObject> DiscardCallAsync(IRequestInput input, IInputPhoneCall peer)
            => InvokeAsync(_discardHandler, input,
                new RequestDiscardCall
                {
                    Peer = peer,
                    Duration = 10,
                    Reason = new TPhoneCallDiscardReasonHangup(),
                    Video = false,
                    ConnectionId = 0
                });

        private static async Task<IObject> InvokeAsync(object handler, IRequestInput input, IObject request)
        {
            var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
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
            return ((TRpcResult)result).Result;
        }

        private static object CreateHandler(string handlerTypeName, params object[] args)
        {
            var assembly = typeof(CallSessionDocument).Assembly;
            var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
            return Activator.CreateInstance(type, args)!;
        }

        /// <summary>Big-endian unsigned DH value guaranteed to sit inside the valid safety range.</summary>
        private static byte[] ValidDhValue(int offset)
        {
            var g = (BigInteger.One << (2048 - 64)) + offset;
            return g.ToByteArray(isUnsigned: true, isBigEndian: true);
        }
    }
}
