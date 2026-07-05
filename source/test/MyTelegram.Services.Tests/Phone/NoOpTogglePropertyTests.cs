using System.Reflection;
using CsCheck;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Property-based test for design Property 11 (No-op rejection).
///
/// <para><b>Property 11:</b> toggling group-call <em>settings</em> or <em>recording</em> to the value
/// that is already current yields <c>GROUPCALL_NOT_MODIFIED</c> and emits no update. The persisted
/// state (including <see cref="GroupCallDocument.Version"/>) is left untouched.</para>
///
/// The property drives the real toggle handlers over an in-memory Mongo store. For every generated
/// initial state it issues a request that asks for the value that is already current:
///   * <c>ToggleGroupCallSettingsHandler</c> - <c>join_muted</c> set to the stored value.
///   * <c>ToggleGroupCallRecordHandler</c>   - <c>start</c> set to match the stored recording state.
///
/// After each no-op request the test asserts:
///   * an <see cref="RpcException"/> with message <c>GROUPCALL_NOT_MODIFIED</c> is thrown,
///   * no update was pushed to any subscriber, and
///   * the persisted document (version + toggled field) is unchanged.
///
/// <b>Validates: Requirements 19.4, 20.5</b>
/// </summary>
public class NoOpTogglePropertyTests
{
    private const long AdminUserId = 1;
    private const long ParticipantUserId = 2;
    private const long CallId = 800;
    private const long AccessHash = 24680;

    /// <summary>
    /// Property 11 (settings): requesting the already-current <c>join_muted</c> value is rejected with
    /// <c>GROUPCALL_NOT_MODIFIED</c>, emits no update, and does not mutate the stored state.
    /// </summary>
    [Fact]
    public void ToggleSettings_ToCurrentJoinMuted_YieldsNotModified_AndEmitsNoUpdate()
    {
        Gen.Bool.Sample(currentJoinMuted =>
        {
            var (store, sender, before) = RunNoOp(joinMuted: currentJoinMuted, recording: false, () =>
                new RequestToggleGroupCallSettings { Call = InputCall(), JoinMuted = currentJoinMuted },
                "ToggleGroupCallSettingsHandler");

            var after = LoadGroupCall(store);
            after.Version.ShouldBe(before.Version);
            after.JoinMuted.ShouldBe(before.JoinMuted);
            sender.Pushes.ShouldBeEmpty();
        });
    }

    /// <summary>
    /// Property 11 (recording): requesting the already-current recording state (<c>start</c> equal to
    /// whether recording is active) is rejected with <c>GROUPCALL_NOT_MODIFIED</c>, emits no update, and
    /// does not mutate the stored state.
    /// </summary>
    [Fact]
    public void ToggleRecord_ToCurrentRecordingState_YieldsNotModified_AndEmitsNoUpdate()
    {
        Gen.Bool.Sample(currentlyRecording =>
        {
            var (store, sender, before) = RunNoOp(joinMuted: false, recording: currentlyRecording, () =>
                new RequestToggleGroupCallRecord { Call = InputCall(), Start = currentlyRecording, Video = false },
                "ToggleGroupCallRecordHandler");

            var after = LoadGroupCall(store);
            after.Version.ShouldBe(before.Version);
            after.RecordStartDate.HasValue.ShouldBe(before.RecordStartDate.HasValue);
            sender.Pushes.ShouldBeEmpty();
        });
    }

    // ---- driver ----------------------------------------------------------------------------------

    /// <summary>
    /// Seeds a group call in the given state, runs <paramref name="requestFactory"/> through the named
    /// handler, asserts it throws <c>GROUPCALL_NOT_MODIFIED</c>, and returns the store / sender together
    /// with the pre-request snapshot of the persisted document.
    /// </summary>
    private static (InMemoryMongoStore Store, CapturingObjectMessageSender Sender, GroupCallDocument Before) RunNoOp(
        bool joinMuted,
        bool recording,
        Func<IObject> requestFactory,
        string handlerTypeName)
    {
        var database = PhoneTestFixtures.CreateDatabase(out var store);
        SeedGroupCall(database, joinMuted: joinMuted, recording: recording);
        var before = LoadGroupCall(store);
        var sender = new CapturingObjectMessageSender();

        var ex = Should.Throw<RpcException>(() =>
            InvokeAsync(handlerTypeName, database, sender, requestFactory()).GetAwaiter().GetResult());
        ex.Message.ShouldBe("GROUPCALL_NOT_MODIFIED");

        return (store, sender, before);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IInputGroupCall InputCall() => new TInputGroupCall { Id = CallId, AccessHash = AccessHash };

    private static void SeedGroupCall(IMongoDatabase database, bool joinMuted, bool recording)
    {
        var collection = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        collection.InsertOne(new GroupCallDocument
        {
            Id = CallId,
            CallId = CallId,
            AccessHash = AccessHash,
            CreatorId = AdminUserId,
            PeerId = AdminUserId,
            PeerType = (int)PeerType.User,
            Active = true,
            JoinMuted = joinMuted,
            Version = 1,
            RecordStartDate = recording ? 1000 : null,
            RecordVideoActive = false,
            Participants = new List<GroupCallParticipantDoc>
            {
                new() { UserId = ParticipantUserId, PeerId = ParticipantUserId, PeerType = (int)PeerType.User, Source = 111 }
            }
        });
    }

    private static GroupCallDocument LoadGroupCall(InMemoryMongoStore store)
    {
        var doc = store.Documents(PhoneTestFixtures.GroupCallsCollectionName).Single();
        return BsonSerializer.Deserialize<GroupCallDocument>(doc);
    }

    private static async Task<IUpdates> InvokeAsync(
        string handlerTypeName,
        IMongoDatabase database,
        IObjectMessageSender sender,
        IObject request)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType($"MyTelegram.Messenger.Handlers.LatestLayer.Phone.{handlerTypeName}", throwOnError: true)!;
        var handler = Activator.CreateInstance(type, database, new PeerHelper(), sender)!;
        var method = type.GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;

        var input = PhoneTestFixtures.RequestInput(AdminUserId).Build();
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
}
