// Feature: push-updates, Property 25: Устройство-источник действия исключается.
//
// For any delivery event carrying a given ExcludeAuthKeyId, the device whose PermAuthKeyId matches
// that value is skipped (no push is dispatched to it), while every other (non-matching) device is
// processed and receives exactly one push.
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes: a stubbed
// IPushDispatcher that records which devices it was invoked for, a query processor returning a set of
// devices with distinct PermAuthKeyId and distinct Tokens, an offline IPushOnlineFilter (so nothing is
// suppressed for being online) and an unlocked IDeviceLockStore. One device's PermAuthKeyId is chosen
// as the LayeredPushMessageCreatedIntegrationEvent.ExcludeAuthKeyId. It then asserts the dispatcher
// was invoked for every device EXCEPT the excluded one.
//
// Validates: Requirements 7.3

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

public class Property25_ExcludeAuthKeyIdTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    /// <summary>
    /// A multi-device delivery scenario for one recipient where every device has a distinct
    /// <c>PermAuthKeyId</c> and a distinct <c>Token</c> (so neither the exclude check nor the
    /// per-token dedup is ambiguous), together with the auth key id chosen to be excluded.
    /// </summary>
    public sealed record ExcludeScenario(
        IReadOnlyList<FakePushDeviceReadModel> Devices,
        long RecipientUserId,
        long ExcludedAuthKeyId)
    {
        public override string ToString() =>
            $"ExcludeScenario(recipient={RecipientUserId}, devices={Devices.Count}, excluded={ExcludedAuthKeyId})";
    }

    private static class ExcludeArbitraries
    {
        // Reuse the task-1 primitive generators (PushGen.PooledUserId, etc.) to compose a device set
        // with guaranteed-distinct PermAuthKeyId and Token values, then nominate one to exclude.
        public static Arbitrary<ExcludeScenario> ExcludeScenario() =>
            Arb.From(
                from recipient in PushGen.PooledUserId
                from count in Gen.Choose(2, 6)
                from excludeIndex in Gen.Choose(0, count - 1)
                from noMuted in Arb.Generate<bool>()
                from appSandbox in Arb.Generate<bool>()
                let devices = BuildDistinctDevices(recipient, count)
                select new ExcludeScenario(devices, recipient, devices[excludeIndex].PermAuthKeyId));

        private static IReadOnlyList<FakePushDeviceReadModel> BuildDistinctDevices(long recipient, int count)
        {
            var devices = new List<FakePushDeviceReadModel>(count);
            for (var i = 0; i < count; i++)
            {
                var token = $"exclude-token-{i}";
                devices.Add(new FakePushDeviceReadModel
                {
                    Id = token,
                    UserId = recipient,
                    // Distinct PermAuthKeyId per device so the exclude filter targets exactly one.
                    PermAuthKeyId = 1000L + i,
                    TokenType = PushTokenTypes.Supported[i % PushTokenTypes.Supported.Count],
                    Token = token,
                    // Plaintext fallback: this property isolates the exclude filter from the
                    // (separately tested) MTProto encryption path.
                    Secret = null,
                    NoMuted = false,
                    AppSandbox = false,
                    OtherUids = Array.Empty<long>()
                });
            }

            return devices;
        }
    }

    // Property 25: Устройство-источник действия исключается
    // Validates: Requirements 7.3
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(ExcludeArbitraries) })]
    public void Originating_device_is_excluded_while_others_are_processed(ExcludeScenario scenario)
    {
        // Arrange
        var queryProcessor = new StubQueryProcessor(scenario.Devices);
        var dispatcher = new RecordingDispatcher();
        var commandBus = new NoopCommandBus();
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
            scenario.RecipientUserId,
            null,
            "default");

        var eventData = new LayeredPushMessageCreatedIntegrationEvent(
            PeerType.User,
            scenario.RecipientUserId,
            ReadOnlyMemory<byte>.Empty,
            ExcludeAuthKeyId: scenario.ExcludedAuthKeyId,
            ExcludeUserId: null,
            OnlySendToUserId: null,
            OnlySendToThisAuthKeyId: null,
            Pts: 0,
            Qts: null,
            GlobalSeqNo: 0,
            PushData: pushData,
            ExcludeUserIds: null);

        // Act
        handler.HandleEventAsync(eventData).GetAwaiter().GetResult();

        // Assert: the excluded device's auth key was never dispatched to.
        dispatcher.DispatchedAuthKeyIds.ShouldNotContain(
            scenario.ExcludedAuthKeyId,
            $"excluded auth key {scenario.ExcludedAuthKeyId} must be skipped");

        // Every non-excluded device received exactly one push (distinct tokens => no dedup loss).
        var expectedAuthKeyIds = scenario.Devices
            .Select(d => d.PermAuthKeyId)
            .Where(id => id != scenario.ExcludedAuthKeyId)
            .ToList();

        dispatcher.DispatchedAuthKeyIds
            .OrderBy(x => x)
            .ShouldBe(expectedAuthKeyIds.OrderBy(x => x),
                $"every device except the excluded one must be processed; excluded={scenario.ExcludedAuthKeyId}");

        dispatcher.Calls.ShouldBe(expectedAuthKeyIds.Count);
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Records the PermAuthKeyId of every device it is invoked for and reports Delivered.</summary>
    private sealed class RecordingDispatcher : IPushDispatcher
    {
        public List<long> DispatchedAuthKeyIds { get; } = new();
        public int Calls => DispatchedAuthKeyIds.Count;

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            DispatchedAuthKeyIds.Add(device.PermAuthKeyId);
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Always reports the device offline so delivery proceeds.</summary>
    private sealed class OfflineFilter : IPushOnlineFilter
    {
        public Task<bool> IsOnlineAsync(long permAuthKeyId) => Task.FromResult(false);
        public Task MarkOnlineAsync(long permAuthKeyId) => Task.CompletedTask;
    }

    /// <summary>Always reports the device unlocked so the payload is not rewritten to LOCKED_MESSAGE.</summary>
    private sealed class UnlockedDeviceLockStore : IDeviceLockStore
    {
        public Task SetAsync(long permAuthKeyId, int periodSeconds) => Task.CompletedTask;
        public Task<bool> IsLockedAsync(long permAuthKeyId) => Task.FromResult(false);
    }

    /// <summary>Accepts and ignores every published command (no unregister is expected here).</summary>
    private sealed class NoopCommandBus : ICommandBus
    {
        public Task<TExecutionResult> PublishAsync<TAggregate, TIdentity, TExecutionResult>(
            ICommand<TAggregate, TIdentity, TExecutionResult> command,
            CancellationToken cancellationToken)
            where TAggregate : IAggregateRoot<TIdentity>
            where TIdentity : IIdentity
            where TExecutionResult : IExecutionResult
            => Task.FromResult((TExecutionResult)(object)ExecutionResult.Success());
    }
}
