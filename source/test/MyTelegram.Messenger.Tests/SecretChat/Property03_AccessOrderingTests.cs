using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Queries;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats, Property 3: Access-control ordering.
///
/// For any request to a handler taking an <c>InputEncryptedChat</c> where several access conditions are
/// violated at once, the returned error is that of the EARLIEST violated check in the fixed order
/// (1) caller type — anonymous/unknown caller yields 403 USER_INVALID and a bot yields 400
/// BOT_METHOD_INVALID, (2) chat resolution, (3) access_hash match, (4) caller membership — the last three
/// all yielding 400 ENCRYPTION_ID_INVALID. At the first failure no further check runs, no state changes
/// and no updates are delivered.
///
/// Validates: Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 5.6, 9.3, 14.2.
///
/// How it is tested: <see cref="SecretChatAccessGen.AccessCase"/> toggles every violable condition
/// independently — caller kind (user / bot / anonymous / unregistered), which side of the chat the caller
/// is (admin or participant), whether the chat resolves, whether the supplied access_hash matches, whether
/// the caller is a member, and the stored chat state (Waiting/Active/Discarded) — so a single property
/// sweeps the whole lattice of overlapping violations. The toggles are weighted so that roughly a fifth of
/// the generated cases violate nothing, which exercises the success path as well. The REAL
/// <see cref="SecretChatAccessResolver"/> is driven, both directly and through the REAL
/// <see cref="SecretChatAppService"/> (via <c>messages.reportEncryptedSpam</c>, the thinnest method whose
/// only precondition is the ordered access check); every collaborator is one of the hand-written harness
/// fakes. The expected error is recomputed independently from the fixed precedence order and compared with
/// the <see cref="RpcException.RpcError"/> actually thrown.
///
/// "No further checks run" is asserted structurally rather than by trusting the error string: the fake
/// query processor records every query it executes, so a caller-type failure must leave the chat lookup
/// unissued (and an anonymous caller must issue no query at all). "No state changes and no updates" is
/// asserted on the recording command bus, the recording update dispatcher and the message store.
/// Each property runs a minimum of 100 generated cases.
/// </summary>
public class Property03_AccessOrderingTests
{
    [Property(Arbitrary = new[] { typeof(SecretChatAccessArbitraries) }, MaxTest = 200)]
    public void Resolver_reports_the_earliest_violated_check_and_stops_there(SecretChatAccessCase @case)
    {
        var world = new World(@case);
        var expectedError = ExpectedFirstError(@case);

        if (expectedError is null)
        {
            var access = world.Resolver.ResolveAsync(world.Input, world.Peer).GetAwaiter().GetResult();

            // No violation: the chat resolves and the caller is bound to the correct side of it,
            // independently of the stored chat state (ResolveAsync never inspects it).
            access.ShouldNotBeNull();
            access.Chat.ChatId.ShouldBe((long)SecretChatTestHarness.ChatId);
            access.CallerIsAdmin.ShouldBe(@case.Role == SecretChatCallerRole.Admin);
            access.CallerUserId.ShouldBe(world.CallerUserId);
            access.OtherUserId.ShouldBe(@case.Role == SecretChatCallerRole.Admin
                ? SecretChatTestHarness.ParticipantId
                : SecretChatTestHarness.AdminId);
        }
        else
        {
            var ex = Should.Throw<RpcException>(() =>
                world.Resolver.ResolveAsync(world.Input, world.Peer).GetAwaiter().GetResult());

            ex.RpcError.ShouldBe(expectedError.Value);
        }

        // The checks that were actually performed: a caller-type failure must not reach the chat lookup,
        // and an anonymous caller (UserId == 0) must not even reach the user lookup.
        world.QueryProcessor.ExecutedQueries.ShouldBe(ExpectedQueryTrace(@case));
    }

    [Property(Arbitrary = new[] { typeof(SecretChatAccessArbitraries) }, MaxTest = 200)]
    public void Service_surfaces_the_same_first_error_and_mutates_nothing_when_it_fails(
        SecretChatAccessCase @case)
    {
        var world = new World(@case);
        var expectedError = ExpectedFirstError(@case);

        if (expectedError is null)
        {
            var result = world.Service.ReportEncryptedSpamAsync(world.Input, world.Peer).GetAwaiter().GetResult();

            result.ShouldBeOfType<TBoolTrue>();
            // The single effect of a fully authorised reportEncryptedSpam is the aggregate command.
            world.CommandBus.Published.Count.ShouldBe(1);
            world.Dispatcher.Dispatched.ShouldBeEmpty();
        }
        else
        {
            var ex = Should.Throw<RpcException>(() =>
                world.Service.ReportEncryptedSpamAsync(world.Input, world.Peer).GetAwaiter().GetResult());

            ex.RpcError.ShouldBe(expectedError.Value);

            // Rejected at the access check: nothing was written and nobody was notified.
            world.CommandBus.Published.ShouldBeEmpty();
            world.Dispatcher.Dispatched.ShouldBeEmpty();
            world.MessageStore.All.ShouldBeEmpty();
        }

        world.QueryProcessor.ExecutedQueries.ShouldBe(ExpectedQueryTrace(@case));
    }

    /// <summary>
    /// The "no further checks run" half of the property, stated as an invariance: clearing every violation
    /// that sits AFTER the first one must not change the outcome. If a later check leaked into the result,
    /// the two runs would disagree.
    /// </summary>
    [Property(Arbitrary = new[] { typeof(SecretChatAccessArbitraries) }, MaxTest = 200)]
    public void Violations_after_the_first_one_do_not_influence_the_result(SecretChatAccessCase @case)
    {
        var withoutLaterViolations = FirstViolation(@case) switch
        {
            ViolationStage.CallerType => @case with
            {
                ChatExists = true, AccessHashMatches = true, CallerIsMember = true
            },
            ViolationStage.ChatResolution => @case with { AccessHashMatches = true, CallerIsMember = true },
            ViolationStage.AccessHash => @case with { CallerIsMember = true },
            _ => @case
        };

        var full = RunResolver(new World(@case));
        var trimmed = RunResolver(new World(withoutLaterViolations));

        full.ShouldBe(trimmed);
    }

    /// <summary>
    /// A hand-picked precedence matrix: each row violates one more condition than the previous one, and the
    /// reported error must stay pinned to the earliest violation.
    /// </summary>
    [Fact]
    public void First_violation_wins_over_every_later_one()
    {
        // Bot caller + missing chat + wrong hash + non-member -> the caller-type check wins.
        RunResolver(new World(Case(SecretChatCallerKind.Bot, chatExists: false, hashMatches: false, member: false)))
            .ShouldBe(RpcErrors.RpcErrors400.BotMethodInvalid);

        // Anonymous caller with everything else valid -> 403 USER_INVALID (not the 400 variant).
        RunResolver(new World(Case(SecretChatCallerKind.Anonymous, chatExists: true, hashMatches: true, member: true)))
            .ShouldBe(RpcErrors.RpcErrors403.UserInvalid);

        // Registered user, unresolvable chat -> chat resolution wins over hash/membership.
        RunResolver(new World(Case(SecretChatCallerKind.User, chatExists: false, hashMatches: false, member: false)))
            .ShouldBe(RpcErrors.RpcErrors400.EncryptionIdInvalid);

        // Chat resolves, bad access_hash -> the hash check wins over membership.
        RunResolver(new World(Case(SecretChatCallerKind.User, chatExists: true, hashMatches: false, member: false)))
            .ShouldBe(RpcErrors.RpcErrors400.EncryptionIdInvalid);

        // Only membership is violated.
        RunResolver(new World(Case(SecretChatCallerKind.User, chatExists: true, hashMatches: true, member: false)))
            .ShouldBe(RpcErrors.RpcErrors400.EncryptionIdInvalid);

        // Nothing is violated.
        RunResolver(new World(Case(SecretChatCallerKind.User, chatExists: true, hashMatches: true, member: true)))
            .ShouldBeNull();
    }

    // ---- Expected-outcome model (derived independently of the production code) ----------------

    private enum ViolationStage
    {
        None,
        CallerType,
        ChatResolution,
        AccessHash,
        Membership
    }

    private static ViolationStage FirstViolation(SecretChatAccessCase @case)
    {
        // (1) caller type: anonymous, unregistered and bot callers are all rejected here.
        if (@case.Caller != SecretChatCallerKind.User)
        {
            return ViolationStage.CallerType;
        }

        // (2) chat resolution.
        if (!@case.ChatExists)
        {
            return ViolationStage.ChatResolution;
        }

        // (3) access_hash match.
        if (!@case.AccessHashMatches)
        {
            return ViolationStage.AccessHash;
        }

        // (4) caller membership.
        if (!@case.CallerIsMember)
        {
            return ViolationStage.Membership;
        }

        return ViolationStage.None;
    }

    private static RpcError? ExpectedFirstError(SecretChatAccessCase @case)
    {
        return FirstViolation(@case) switch
        {
            ViolationStage.None => null,
            ViolationStage.CallerType => @case.Caller == SecretChatCallerKind.Bot
                ? RpcErrors.RpcErrors400.BotMethodInvalid
                : RpcErrors.RpcErrors403.UserInvalid,
            _ => RpcErrors.RpcErrors400.EncryptionIdInvalid
        };
    }

    /// <summary>The queries the resolver is allowed to issue before it short-circuits.</summary>
    private static string[] ExpectedQueryTrace(SecretChatAccessCase @case)
    {
        if (@case.Caller == SecretChatCallerKind.Anonymous)
        {
            // UserId == 0 is rejected without touching the database at all.
            return [];
        }

        if (@case.Caller != SecretChatCallerKind.User)
        {
            // Unregistered caller or bot: the caller check fails, the chat is never looked up.
            return [nameof(GetUserByIdQuery)];
        }

        return [nameof(GetUserByIdQuery), nameof(GetEncryptedChatByIdQuery)];
    }

    // ---- Drivers -----------------------------------------------------------------------------

    /// <summary>Runs the real resolver and returns the raised RPC error, or null when it succeeded.</summary>
    private static RpcError? RunResolver(World world)
    {
        try
        {
            world.Resolver.ResolveAsync(world.Input, world.Peer).GetAwaiter().GetResult();

            return null;
        }
        catch (RpcException ex)
        {
            return ex.RpcError;
        }
    }

    private static SecretChatAccessCase Case(SecretChatCallerKind caller, bool chatExists, bool hashMatches,
        bool member)
    {
        return new SecretChatAccessCase(caller, SecretChatCallerRole.Participant, chatExists, hashMatches, member,
            ChatState.Active);
    }

    /// <summary>
    /// One fully wired secret-chat world for a generated case: the real
    /// <see cref="SecretChatAccessResolver"/> and <see cref="SecretChatAppService"/> over the harness fakes.
    /// </summary>
    private sealed class World
    {
        /// <summary>A registered user who is neither the admin nor the participant of the chat.</summary>
        private const long OutsiderId = 3003;

        private const long OutsiderPermAuthKeyId = 333;

        public World(SecretChatAccessCase @case)
        {
            CallerUserId = @case.Caller == SecretChatCallerKind.Anonymous
                ? 0
                : @case.CallerIsMember
                    ? @case.Role == SecretChatCallerRole.Admin
                        ? SecretChatTestHarness.AdminId
                        : SecretChatTestHarness.ParticipantId
                    : OutsiderId;

            var callerPermAuthKeyId = @case.CallerIsMember
                ? @case.Role == SecretChatCallerRole.Admin
                    ? SecretChatTestHarness.AdminPermAuthKeyId
                    : SecretChatTestHarness.ParticipantPermAuthKeyId
                : OutsiderPermAuthKeyId;

            // An "unknown" caller is deliberately left out of the user table; a bot caller is the same
            // identity flagged as a bot, so the caller-type toggle is orthogonal to membership.
            if (@case.Caller is SecretChatCallerKind.User or SecretChatCallerKind.Bot)
            {
                QueryProcessor.Users[CallerUserId] =
                    FakeUser.Create(CallerUserId, bot: @case.Caller == SecretChatCallerKind.Bot);
            }

            if (@case.ChatExists)
            {
                QueryProcessor.Chats[SecretChatTestHarness.ChatId] = SecretChatTestHarness.Chat(@case.State);
            }

            Input = SecretChatTestHarness.Input(CallerUserId, callerPermAuthKeyId);
            Peer = SecretChatTestHarness.InputChat(@case.AccessHashMatches
                ? SecretChatTestHarness.AccessHash
                : SecretChatTestHarness.AccessHash + 1);

            Resolver = new SecretChatAccessResolver(QueryProcessor);
            Service = new SecretChatAppService(CommandBus,
                QueryProcessor,
                new FakeIdGenerator(),
                new FakeBlockCacheAppService(),
                Resolver,
                Dispatcher,
                MessageStore,
                new InMemorySecretChatRequestLedger(),
                new InMemoryEncryptedFileStore(),
                SecretChatTestHarness.ChatConverters(),
                SecretChatTestHarness.MessageConverters(),
                SecretChatTestHarness.FileConverters());
        }

        public long CallerUserId { get; }
        public FakeQueryProcessor QueryProcessor { get; } = new();
        public RecordingCommandBus CommandBus { get; } = new();
        public RecordingUpdateDispatcher Dispatcher { get; } = new();
        public InMemorySecretChatMessageStore MessageStore { get; } = new();
        public SecretChatAccessResolver Resolver { get; }
        public SecretChatAppService Service { get; }
        public TestRequestInput Input { get; }
        public IInputEncryptedChat Peer { get; }
    }
}

/// <summary>How the caller of a secret-chat request is classified for the access-ordering property.</summary>
public enum SecretChatCallerKind
{
    /// <summary>A registered, non-bot user: passes the caller-type check.</summary>
    User,

    /// <summary>A registered bot: rejected with BOT_METHOD_INVALID.</summary>
    Bot,

    /// <summary>No authenticated user on the connection (UserId == 0): rejected with USER_INVALID.</summary>
    Anonymous,

    /// <summary>An authenticated id with no user read model: rejected with USER_INVALID.</summary>
    Unknown
}

/// <summary>Which side of the chat the caller claims to be, when it is a member at all.</summary>
public enum SecretChatCallerRole
{
    Admin,
    Participant
}

/// <summary>
/// A secret-chat access-control case. Each violable condition is an independent toggle so the generator
/// covers the full lattice of simultaneous violations; <see cref="State"/> is generated as well to pin down
/// that chat state never participates in the access ordering.
/// </summary>
public sealed record SecretChatAccessCase(
    SecretChatCallerKind Caller,
    SecretChatCallerRole Role,
    bool ChatExists,
    bool AccessHashMatches,
    bool CallerIsMember,
    ChatState State)
{
    public override string ToString()
    {
        return $"AccessCase(caller={Caller}, role={Role}, chatExists={ChatExists}, " +
               $"hashMatches={AccessHashMatches}, member={CallerIsMember}, state={State})";
    }
}

/// <summary>Generators for the secret-chat access-ordering property.</summary>
public static class SecretChatAccessGen
{
    /// <summary>
    /// A boolean biased towards <c>true</c> (3:1) so that a meaningful share of generated cases violates
    /// nothing at all and the success path is exercised, while every failure combination stays reachable.
    /// </summary>
    private static Gen<bool> MostlyTrue =>
        Gen.Frequency(Tuple.Create(3, Gen.Constant(true)), Tuple.Create(1, Gen.Constant(false)));

    private static Gen<SecretChatCallerKind> CallerKind =>
        Gen.Frequency(Tuple.Create(3, Gen.Constant(SecretChatCallerKind.User)),
            Tuple.Create(1, Gen.Constant(SecretChatCallerKind.Bot)),
            Tuple.Create(1, Gen.Constant(SecretChatCallerKind.Anonymous)),
            Tuple.Create(1, Gen.Constant(SecretChatCallerKind.Unknown)));

    public static Gen<SecretChatAccessCase> AccessCase =>
        from caller in CallerKind
        from role in Gen.Elements(SecretChatCallerRole.Admin, SecretChatCallerRole.Participant)
        from chatExists in MostlyTrue
        from accessHashMatches in MostlyTrue
        from callerIsMember in MostlyTrue
        from state in Gen.Elements(ChatState.Waiting, ChatState.Active, ChatState.Discarded)
        select new SecretChatAccessCase(caller, role, chatExists, accessHashMatches, callerIsMember, state);
}

/// <summary>FsCheck arbitrary registration surface for the access-ordering property.</summary>
public static class SecretChatAccessArbitraries
{
    public static Arbitrary<SecretChatAccessCase> AccessCase() => Arb.From(SecretChatAccessGen.AccessCase);
}
