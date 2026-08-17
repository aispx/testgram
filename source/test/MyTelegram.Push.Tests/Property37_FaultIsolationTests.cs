// Feature: push-updates, Property 37: A failure on one device does not stop delivery to the others.
//
// For any set of devices belonging to one recipient, if delivery to one device throws an exception,
// the delivery service (PushNotificationEventHandler) still attempts delivery to every remaining
// device: the number of delivery attempts to the other devices equals their count. This is the
// per-device try/catch fault-isolation guarantee (Req 12.1).
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes (the same
// wiring pattern as Property 8 / Property 34): a stubbed IQueryProcessor returning the device set
// verbatim, an offline IPushOnlineFilter (so every device is deliverable), an unlocked
// IDeviceLockStore, a capturing ICommandBus, and a ThrowingDispatcher that throws for ONE selected
// device's token and records the Token for every other send. It then asserts that each remaining
// device was still attempted exactly once.
//
// Each device gets a distinct PermAuthKeyId (so neither the exclude nor the online filter collapses
// two devices), a distinct non-empty Token (so the dedup-by-token logic never suppresses a device),
// a null Secret (isolate from the separately-tested MTProto payload-encryption path), and is stamped
// to the recipient so routing is irrelevant. The set is normalised to at least two devices so the
// isolation guarantee is exercised non-vacuously.
//
// Validates: Requirements 12.1

using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTelegram.Core;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.EventHandlers;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel;
using MyTelegram.Services.Services;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property37_FaultIsolationTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 37: A failure on one device does not stop delivery to the others
    // Validates: Requirements 12.1
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Failure_on_one_device_does_not_stop_delivery_to_the_rest(DeviceSet deviceSet)
    {
        // Arrange: normalise the generated set into >= 2 devices for one recipient, each with a
        // distinct PermAuthKeyId and distinct non-empty Token so the ONLY behaviour exercised is the
        // per-device fault isolation (no exclude/online/dedup filter can drop a device):
        var recipient = deviceSet.RecipientUserId;
        var deviceCount = Math.Max(2, deviceSet.Devices.Count);
        var devices = Enumerable.Range(0, deviceCount)
            .Select(i =>
            {
                var src = deviceSet.Devices[i % deviceSet.Devices.Count];
                var token = $"token-{i}";
                return new FakePushDeviceReadModel
                {
                    Id = token,
                    UserId = recipient,
                    PermAuthKeyId = i + 1,
                    TokenType = src.TokenType,
                    Token = token,
                    Secret = null,
                    NoMuted = src.NoMuted,
                    AppSandbox = src.AppSandbox,
                    OtherUids = src.OtherUids
                };
            })
            .ToList();

        // The dispatcher will throw for exactly one selected device; every other device must still
        // be attempted.
        var throwingToken = devices[0].Token;
        var remainingTokens = devices
            .Where(d => !string.Equals(d.Token, throwingToken, StringComparison.Ordinal))
            .Select(d => d.Token)
            .ToList();

        var queryProcessor = new StubQueryProcessor(devices);
        var dispatcher = new ThrowingDispatcher(throwingToken);
        var commandBus = new CapturingCommandBus();
        var options = Options.Create(new MyTelegramMessengerServerOptions
        {
            Push = new PushConfig { Enabled = true }
        });

        var handler = new PushNotificationEventHandler(
            queryProcessor,
            dispatcher,
            new OfflineFilter(),
            new UnlockedDeviceLockStore(),
            commandBus,
            MtpHelper,
            AuthKeyIdHelper,
            options,
            NullLogger<PushNotificationEventHandler>.Instance);

        var pushData = new PushData(
            PushNotificationTypes.MessageText,
            new[] { "Alice", "hello" },
            recipient,
            null,
            "default");

        var eventData = new LayeredPushMessageCreatedIntegrationEvent(
            PeerType.User,
            recipient,
            ReadOnlyMemory<byte>.Empty,
            ExcludeAuthKeyId: null,
            ExcludeUserId: null,
            OnlySendToUserId: null,
            OnlySendToThisAuthKeyId: null,
            Pts: 0,
            Qts: null,
            GlobalSeqNo: 0,
            PushData: pushData,
            ExcludeUserIds: null);

        // Act: the exception thrown for the selected device must not propagate (the handler swallows
        // it inside its per-device try/catch) nor stop the loop.
        Should.NotThrow(() => handler.HandleEventAsync(eventData).GetAwaiter().GetResult());

        // Assert: every remaining device was still attempted exactly once.
        var attempted = dispatcher.AttemptedTokens.Distinct(StringComparer.Ordinal).ToList();

        attempted.Count.ShouldBe(
            remainingTokens.Count,
            $"throwingToken={throwingToken}, devices=[{string.Join(",", devices.Select(d => d.Token))}]");

        foreach (var token in remainingTokens)
        {
            dispatcher.AttemptedTokens.ShouldContain(
                token,
                $"device '{token}' must still receive a delivery attempt despite the failure on '{throwingToken}'");
        }

        // And the failing device never recorded a (successful) attempt: its send threw.
        dispatcher.AttemptedTokens.ShouldNotContain(throwingToken);
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Throws for the selected device token (simulating a provider/transport fault) and
    /// records the Token for every other send so isolation can be asserted.</summary>
    private sealed class ThrowingDispatcher(string throwingToken) : IPushDispatcher
    {
        public List<string> AttemptedTokens { get; } = new();

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            if (string.Equals(device.Token, throwingToken, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"simulated delivery failure for token '{device.Token}'");
            }

            AttemptedTokens.Add(device.Token);
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Always reports the device offline so delivery proceeds.</summary>
    private sealed class OfflineFilter : IPushOnlineFilter
    {
        public Task<bool> IsOnlineAsync(long permAuthKeyId) => Task.FromResult(false);
        public Task MarkOnlineAsync(long permAuthKeyId) => Task.CompletedTask;
    }

    /// <summary>Always reports the device unlocked so the payload is not rewritten.</summary>
    private sealed class UnlockedDeviceLockStore : IDeviceLockStore
    {
        public Task SetAsync(long permAuthKeyId, int periodSeconds) => Task.CompletedTask;
        public Task<bool> IsLockedAsync(long permAuthKeyId) => Task.FromResult(false);
    }

    /// <summary>Captures every published command (no commands are expected in this property).</summary>
    private sealed class CapturingCommandBus : ICommandBus
    {
        public List<(string TypeName, object AggregateId)> Published { get; } = new();

        public Task<TExecutionResult> PublishAsync<TAggregate, TIdentity, TExecutionResult>(
            ICommand<TAggregate, TIdentity, TExecutionResult> command,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult
        {
            Published.Add((command.GetType().Name, command.AggregateId!));
            return Task.FromResult((TExecutionResult)(object)ExecutionResult.Success());
        }
    }
}
