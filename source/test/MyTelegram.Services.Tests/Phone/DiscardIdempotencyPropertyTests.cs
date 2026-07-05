using System.Reflection;
using CsCheck;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 2 (Discard idempotency).
///
/// Property 2: Invoking <c>phone.discardCall</c> on an already-discarded Call_Session returns the
/// same <c>phoneCallDiscarded</c> (same reason / duration) as the original discard and does not
/// mutate the recorded session state (and emits no additional updates / service messages).
///
/// The property is exercised across randomly generated initial states, first-discard inputs, and a
/// (deliberately different) second-discard input: no matter what the re-discard carries, the response
/// must reflect the state recorded by the first discard and the stored document must be untouched.
///
/// **Validates: Requirements 7.9**
/// </summary>
public class DiscardIdempotencyPropertyTests
{
    private const long CallId = 42;
    private const long CallerId = 1;
    private const long CalleeId = 2;
    private const long CallerAccessHash = 111;
    private const long CalleeAccessHash = 222;

    [Fact]
    public void ReDiscard_ReturnsSameDiscardedCall_AndDoesNotMutateState()
    {
        // A session may already be in any pre-discard state; the first discard closes it, the second is
        // the idempotent re-discard. The two discard requests carry independently generated payloads so
        // that we prove the re-discard ignores its own input and echoes the recorded discard.
        var gen =
            from initialState in Gen.OneOfConst("requested", "waiting", "received", "accepted", "confirmed")
            from initialVideo in Gen.Bool
            from firstKind in Gen.Int[0, 4]
            from firstSlug in Gen.OneOfConst("", "conf-1", "slug_xyz", "migrate-target")
            from firstDuration in Gen.Int[0, 100_000]
            from firstVideo in Gen.Bool
            from secondKind in Gen.Int[0, 4]
            from secondSlug in Gen.OneOfConst("", "other", "second-slug")
            from secondDuration in Gen.Int[0, 100_000]
            from secondVideo in Gen.Bool
            select new DiscardScenario(
                initialState,
                initialVideo,
                firstKind,
                firstSlug,
                firstDuration,
                firstVideo,
                secondKind,
                secondSlug,
                secondDuration,
                secondVideo);

        gen.Sample(scenario => RunScenario(scenario), iter: 200);
    }

    private static void RunScenario(DiscardScenario scenario)
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        var collection = database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
        collection.InsertOne(new CallSessionDocument
        {
            Id = CallId,
            CallId = CallId,
            CallerId = CallerId,
            CalleeId = CalleeId,
            CallerAccessHash = CallerAccessHash,
            CalleeAccessHash = CalleeAccessHash,
            State = scenario.InitialState,
            Video = scenario.InitialVideo
        });

        var sender = new CapturingObjectMessageSender();
        var messageApp = new FakeMessageAppService();
        var userConverter = CreateUserConverter();
        var accessHash = new FakeAccessHashHelper2();
        var input = PhoneTestFixtures.RequestInput(CallerId).Build();

        // First discard - transitions the session to "discarded" and records reason/duration/flags.
        var firstUpdates = InvokeDiscardAsync(
            database, userConverter, sender, messageApp, accessHash, input,
            BuildRequest(scenario.FirstKind, scenario.FirstSlug, scenario.FirstDuration, scenario.FirstVideo))
            .GetAwaiter().GetResult();
        var firstDiscarded = ExtractDiscarded(firstUpdates);

        // Snapshot the recorded state and the side effects observed so far.
        var storedAfterFirst = store.Documents(PhoneTestFixtures.CallSessionsCollectionName).Single().ToString();
        var pushesAfterFirst = sender.Pushes.Count;
        var messagesAfterFirst = messageApp.SentMessages.Count;

        // Re-discard the already-discarded session with a deliberately different payload.
        var secondUpdates = InvokeDiscardAsync(
            database, userConverter, sender, messageApp, accessHash, input,
            BuildRequest(scenario.SecondKind, scenario.SecondSlug, scenario.SecondDuration, scenario.SecondVideo))
            .GetAwaiter().GetResult();
        var secondDiscarded = ExtractDiscarded(secondUpdates);

        // R7.9: the re-discard echoes the phoneCallDiscarded recorded by the FIRST discard, regardless of
        // what the second request carried.
        secondDiscarded.Id.ShouldBe(firstDiscarded.Id);
        NormalizeReason(secondDiscarded.Reason).ShouldBe(NormalizeReason(firstDiscarded.Reason));
        secondDiscarded.Duration.ShouldBe(firstDiscarded.Duration);
        secondDiscarded.NeedRating.ShouldBe(firstDiscarded.NeedRating);
        secondDiscarded.NeedDebug.ShouldBe(firstDiscarded.NeedDebug);
        secondDiscarded.Video.ShouldBe(firstDiscarded.Video);

        // R7.9: recorded state is not mutated by the re-discard...
        var storedAfterSecond = store.Documents(PhoneTestFixtures.CallSessionsCollectionName).Single().ToString();
        storedAfterSecond.ShouldBe(storedAfterFirst);

        // ...and no additional updates or service messages are emitted for the idempotent re-discard.
        sender.Pushes.Count.ShouldBe(pushesAfterFirst);
        messageApp.SentMessages.Count.ShouldBe(messagesAfterFirst);
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

    private static RequestDiscardCall BuildRequest(int kind, string slug, int duration, bool video) => new()
    {
        Peer = new TInputPhoneCall { Id = CallId, AccessHash = CallerAccessHash },
        Duration = duration,
        Reason = CreateReason(kind, slug),
        Video = video,
        ConnectionId = 0
    };

    private static IPhoneCallDiscardReason CreateReason(int kind, string slug) => kind switch
    {
        0 => new TPhoneCallDiscardReasonMissed(),
        1 => new TPhoneCallDiscardReasonDisconnect(),
        2 => new TPhoneCallDiscardReasonHangup(),
        3 => new TPhoneCallDiscardReasonBusy(),
        _ => new TPhoneCallDiscardReasonMigrateConferenceCall { Slug = slug }
    };

    private static string NormalizeReason(IPhoneCallDiscardReason? reason) => reason switch
    {
        TPhoneCallDiscardReasonMissed => "missed",
        TPhoneCallDiscardReasonDisconnect => "disconnect",
        TPhoneCallDiscardReasonHangup => "hangup",
        TPhoneCallDiscardReasonBusy => "busy",
        TPhoneCallDiscardReasonMigrateConferenceCall migrate => $"migrate:{migrate.Slug}",
        _ => "none"
    };

    private static TPhoneCallDiscarded ExtractDiscarded(IUpdates updates)
    {
        var tUpdates = updates.ShouldBeOfType<TUpdates>();
        var update = tUpdates.Updates.OfType<TUpdatePhoneCall>().ShouldHaveSingleItem();
        return update.PhoneCall.ShouldBeOfType<TPhoneCallDiscarded>();
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

    private sealed record DiscardScenario(
        string InitialState,
        bool InitialVideo,
        int FirstKind,
        string FirstSlug,
        int FirstDuration,
        bool FirstVideo,
        int SecondKind,
        string SecondSlug,
        int SecondDuration,
        bool SecondVideo);
}
