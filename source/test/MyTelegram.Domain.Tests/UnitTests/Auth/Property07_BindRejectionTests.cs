// Feature: auth-methods-completion, Property 7: Bind rejects missing perm keys and invalid binding messages
//
// For any bind request whose encrypted binding message is empty or inconsistent (mismatched
// temp/perm key id or nonce, or unparsable), bindTempAuthKey raises ENCRYPTED_MESSAGE_INVALID;
// for any bind request referencing a Perm_Auth_Key with no existing session, it raises
// AUTH_KEY_PERM_EMPTY; in both cases no binding is recorded (no command is published).
//
// Validates: Requirements 3.4, 3.5
//
// Approach: this single parametric property drives the production (internal) BindTempAuthKeyHandler
// via reflection (mirroring Property 1/2/3/4/6) with hand-rolled fakes (a query processor that
// returns either a device read model or null for GetDeviceByAuthKeyIdQuery, and a capturing command
// bus). Each generated case is one of two documented rejection families:
//
//   * A "message invalid" family (RejectionKind.EmptyMessage / UnparsableMessage /
//     MismatchedTempKey / MismatchedPermKey / MismatchedNonce). Here the encrypted binding message
//     is empty, unparsable, or parses to a TBindAuthKeyInner whose ids/nonce disagree with the
//     request/connection. The handler must raise 400 ENCRYPTED_MESSAGE_INVALID. For these cases the
//     query processor is wired to return a PRESENT device, proving the message check fires first
//     (before any perm-key lookup) and no command is published.
//   * A "missing perm key" family (RejectionKind.MissingPermKey). Here the encrypted binding
//     message is fully valid and consistent (temp/perm/nonce all match), but the perm key resolves
//     to NO session (query returns null). The handler must raise 401 AUTH_KEY_PERM_EMPTY.
//
// In every case the property asserts the exact documented (code, type) and that the capturing
// command bus published nothing (no binding recorded).

using System.Reflection;
using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Auth;
using MyTelegram.Schema.Extensions;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property07_BindRejectionTests
{
    // Property 7: Bind rejects missing perm keys and invalid binding messages
    // Validates: Requirements 3.4, 3.5
    [Property(Arbitrary = new[] { typeof(BindRejectionArbitraries) }, MaxTest = 100)]
    public void Bind_rejects_missing_perm_keys_and_invalid_binding_messages(BindRejectionCase testCase)
    {
        // Arrange: build the encrypted binding message and the device lookup result for this case.
        var encryptedMessage = BuildEncryptedMessage(testCase);
        var device = testCase.Kind == RejectionKind.MissingPermKey
            ? null
            : new FakeDeviceReadModel
            {
                Id = testCase.PermAuthKeyId.ToString(),
                PermAuthKeyId = testCase.PermAuthKeyId,
                TempAuthKeyId = 0,
                UserId = 1L
            };

        var queryProcessor = new StubQueryProcessor(device);
        var commandBus = new CapturingCommandBus();

        // NOTE: BindTempAuthKeyHandler(ICommandBus commandBus, IQueryProcessor queryProcessor)
        // -- commandBus is the FIRST constructor argument.
        var handler = CreateMessengerHandler(
            "MyTelegram.Messenger.Handlers.LatestLayer.Auth.BindTempAuthKeyHandler",
            commandBus,
            queryProcessor);

        var request = new RequestBindTempAuthKey
        {
            PermAuthKeyId = testCase.PermAuthKeyId,
            Nonce = testCase.Nonce,
            ExpiresAt = testCase.ExpiresAt,
            EncryptedMessage = encryptedMessage
        };

        var input = CreateRequestInput(testCase.AuthKeyId);

        // Act + Assert: the documented RpcError is raised and no command is published.
        var ex = Should.Throw<RpcException>(() => InvokeAsync(handler, input, request));

        if (testCase.Kind == RejectionKind.MissingPermKey)
        {
            // Requirement 3.4: perm key with no existing session -> 401 AUTH_KEY_PERM_EMPTY.
            ex.RpcError.ErrorCode.ShouldBe(401);
            ex.RpcError.Message.ShouldBe("AUTH_KEY_PERM_EMPTY");
        }
        else
        {
            // Requirement 3.5: empty/unparsable/inconsistent binding message -> 400 ENCRYPTED_MESSAGE_INVALID.
            ex.RpcError.ErrorCode.ShouldBe(400);
            ex.RpcError.Message.ShouldBe("ENCRYPTED_MESSAGE_INVALID");
        }

        // In both families no binding is recorded.
        commandBus.Published.Count.ShouldBe(0);
    }

    /// <summary>Builds the EncryptedMessage payload for the case: empty, unparsable garbage, or a
    /// serialized TBindAuthKeyInner that is either fully consistent (MissingPermKey family) or has a
    /// single deliberately mismatched field.</summary>
    private static ReadOnlyMemory<byte> BuildEncryptedMessage(BindRejectionCase testCase)
    {
        switch (testCase.Kind)
        {
            case RejectionKind.EmptyMessage:
                return ReadOnlyMemory<byte>.Empty;

            case RejectionKind.UnparsableMessage:
                // Too few bytes / wrong constructor id to parse as a TBindAuthKeyInner. The handler
                // wraps the parse in try/catch and treats a failure as an invalid message.
                return testCase.GarbageBytes;

            default:
                // A well-formed TBindAuthKeyInner. For the consistent (MissingPermKey) case every id
                // and the nonce match the request/connection; for the mismatch cases exactly one
                // field is perturbed so the handler's consistency check fails.
                var inner = new TBindAuthKeyInner
                {
                    Nonce = testCase.Kind == RejectionKind.MismatchedNonce
                        ? testCase.Nonce + 1
                        : testCase.Nonce,
                    TempAuthKeyId = testCase.Kind == RejectionKind.MismatchedTempKey
                        ? testCase.AuthKeyId + 1
                        : testCase.AuthKeyId,
                    PermAuthKeyId = testCase.Kind == RejectionKind.MismatchedPermKey
                        ? testCase.PermAuthKeyId + 1
                        : testCase.PermAuthKeyId,
                    TempSessionId = 42L,
                    ExpiresAt = testCase.ExpiresAt
                };
                return inner.ToBytes();
        }
    }

    private static IObject InvokeAsync(object handler, IRequestInput input, IObject request)
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

        return ((Task<IObject>)taskObj).GetAwaiter().GetResult();
    }

    private static RequestInput CreateRequestInput(long authKeyId)
    {
        return new RequestInput(
            ConnectionId: "test-connection",
            ConnectionType: default,
            RequestId: Guid.NewGuid(),
            ObjectId: 0u,
            ReqMsgId: 1L,
            SeqNumber: 0,
            UserId: 1L,
            AuthKeyId: authKeyId,
            PermAuthKeyId: authKeyId,
            Layer: 0,
            Date: 0L,
            DeviceType: default,
            ClientIp: "127.0.0.1",
            SessionId: 1L,
            AccessHashKeyId: 0L);
    }

    /// <summary>Reflectively constructs an internal sealed handler from the Messenger assembly.</summary>
    private static object CreateMessengerHandler(string typeName, params object[] args)
    {
        var assembly = typeof(MyTelegramMessengerServerOptions).Assembly;
        var type = assembly.GetType(typeName, throwOnError: true)!;
        return Activator.CreateInstance(type, args)!;
    }

    /// <summary>Returns the configured device (or null) for the GetDeviceByAuthKeyIdQuery the handler
    /// issues.</summary>
    private sealed class StubQueryProcessor(IDeviceReadModel? device) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)device!);
    }

    /// <summary>Captures published commands so the test can assert none were published.</summary>
    private sealed class CapturingCommandBus : ICommandBus
    {
        public List<object> Published { get; } = new();

        public Task<TExecutionResult> PublishAsync<TAggregate, TIdentity, TExecutionResult>(
            ICommand<TAggregate, TIdentity, TExecutionResult> command,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult
        {
            Published.Add(command);
            return Task.FromResult((TExecutionResult)(object)ExecutionResult.Success());
        }
    }

    private sealed class FakeDeviceReadModel : IDeviceReadModel
    {
        public int ApiId { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public int DateActive { get; set; }
        public int DateCreated { get; set; }
        public string DeviceModel { get; set; } = string.Empty;
        public long Hash { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Ip { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string LangCode { get; set; } = string.Empty;
        public string LangPack { get; set; } = string.Empty;
        public int Layer { get; set; }
        public bool OfficialApp { get; set; }
        public bool PasswordPending { get; set; }
        public long PermAuthKeyId { get; set; }
        public string Platform { get; set; } = string.Empty;
        public string SystemLangCode { get; set; } = string.Empty;
        public string SystemVersion { get; set; } = string.Empty;
        public long TempAuthKeyId { get; set; }
        public long UserId { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
    }
}

/// <summary>The rejection family for a bind request. The first five produce 400
/// ENCRYPTED_MESSAGE_INVALID (Requirement 3.5); MissingPermKey produces 401 AUTH_KEY_PERM_EMPTY
/// (Requirement 3.4).</summary>
public enum RejectionKind
{
    EmptyMessage,
    UnparsableMessage,
    MismatchedTempKey,
    MismatchedPermKey,
    MismatchedNonce,
    MissingPermKey
}

/// <summary>Input case for Property 7: the rejection family, the connection auth key id (the temp
/// key id the request runs under), the perm auth key id and nonce/expiry carried by the request, and
/// a garbage byte payload used only by the UnparsableMessage family.</summary>
public sealed record BindRejectionCase(
    RejectionKind Kind,
    long AuthKeyId,
    long PermAuthKeyId,
    long Nonce,
    int ExpiresAt,
    byte[] GarbageBytes);

/// <summary>FsCheck arbitrary surface for Property 7. Generates every rejection family paired with
/// positive auth/perm key ids, an arbitrary nonce and a positive expiry, plus a short (0-7 byte)
/// garbage payload that cannot deserialize into a TBindAuthKeyInner (which needs 40 bytes).</summary>
public static class BindRejectionArbitraries
{
    public static Arbitrary<BindRejectionCase> BindRejectionCase()
    {
        var kindGen = Gen.Elements(
            RejectionKind.EmptyMessage,
            RejectionKind.UnparsableMessage,
            RejectionKind.MismatchedTempKey,
            RejectionKind.MismatchedPermKey,
            RejectionKind.MismatchedNonce,
            RejectionKind.MissingPermKey);

        var authKeyGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        var permKeyGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        var nonceGen = Arb.Default.Int64().Generator;
        var expireGen = Gen.Choose(1, int.MaxValue);

        // 0..7 bytes: far too short to parse as a 40-byte TBindAuthKeyInner, so ToTObject fails.
        var garbageGen =
            from len in Gen.Choose(0, 7)
            from bytes in Gen.ArrayOf(len, Gen.Choose(0, 255).Select(i => (byte)i))
            select bytes;

        var gen =
            from kind in kindGen
            from authKey in authKeyGen
            from permKey in permKeyGen
            from nonce in nonceGen
            from expire in expireGen
            from garbage in garbageGen
            select new BindRejectionCase(kind, authKey, permKey, nonce, expire, garbage);

        return Arb.From(gen);
    }
}
