// Feature: push-updates, Property 24: An online device is suppressed.
//
// For any device for which PushOnlineFilter.IsOnlineAsync(PermAuthKeyId) returns true, the delivery
// service (PushNotificationEventHandler) does NOT send a push to that device; conversely, when the
// filter reports the device offline (returns false), the delivery service sends the push.
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes: a stubbed
// IPushDispatcher that counts invocations, a query processor returning exactly one device, an
// IPushOnlineFilter whose IsOnlineAsync returns a GENERATED bool, an unlocked IDeviceLockStore, and
// a capturing ICommandBus. It then asserts the biconditional: the dispatcher is invoked iff the
// device is reported offline (online == false).
//
// Validates: Requirements 7.1

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

public class Property24_OnlineDeviceSuppressedTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 24: An online device is suppressed
    // Validates: Requirements 7.1
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Online_device_is_suppressed_offline_device_is_sent(
        DeviceRegistration reg,
        bool online)
    {
        // Arrange: a single registered device addressable to its owner. The secret is left null so
        // the encryptor takes the plaintext fallback: this property is about the online filter's
        // suppression behaviour (Req 7.1), so it must be isolated from the (separately tested)
        // MTProto payload-encryption path.
        var device = new FakePushDeviceReadModel
        {
            Id = reg.Token,
            UserId = reg.UserId,
            PermAuthKeyId = reg.PermAuthKeyId,
            TokenType = reg.TokenType,
            Token = reg.Token,
            Secret = null,
            NoMuted = reg.NoMuted,
            AppSandbox = reg.AppSandbox,
            OtherUids = reg.OtherUids
        };

        var queryProcessor = new StubQueryProcessor(new IPushDeviceReadModel[] { device });
        var dispatcher = new StubDispatcher();
        var commandBus = new CapturingCommandBus();
        var options = Options.Create(new MyTelegramMessengerServerOptions
        {
            Push = new PushConfig { Enabled = true }
        });

        var handler = new PushNotificationEventHandler(
            queryProcessor,
            dispatcher,
            new ConfigurableOnlineFilter(online),
            new UnlockedDeviceLockStore(),
            commandBus,
            MtpHelper,
            AuthKeyIdHelper,
            options,
            NullLogger<PushNotificationEventHandler>.Instance);

        // A new-message notification addressed to the device owner. UserId != 0 so ResolveUserId
        // returns it directly; no ExcludeAuthKeyId so the device is not skipped by exclusion.
        var pushData = new PushData(
            PushNotificationTypes.MessageText,
            new[] { "Alice", "hello" },
            reg.UserId,
            null,
            "default");

        var eventData = new LayeredPushMessageCreatedIntegrationEvent(
            PeerType.User,
            reg.UserId,
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

        // Act
        handler.HandleEventAsync(eventData).GetAwaiter().GetResult();

        // Assert: an online device is suppressed (no send); an offline device receives exactly one send.
        if (online)
        {
            dispatcher.Calls.ShouldBe(0,
                $"online device (PermAuthKeyId={device.PermAuthKeyId}) must be suppressed");
        }
        else
        {
            dispatcher.Calls.ShouldBe(1,
                $"offline device (PermAuthKeyId={device.PermAuthKeyId}) must be sent the push");
        }
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Counts send invocations; reports every send as delivered.</summary>
    private sealed class StubDispatcher : IPushDispatcher
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Reports the configured online state for the device's PermAuthKeyId.</summary>
    private sealed class ConfigurableOnlineFilter(bool online) : IPushOnlineFilter
    {
        public Task<bool> IsOnlineAsync(long permAuthKeyId) => Task.FromResult(online);
        public Task MarkOnlineAsync(long permAuthKeyId) => Task.CompletedTask;
    }

    /// <summary>Always reports the device unlocked so the payload is not rewritten to LOCKED_MESSAGE.</summary>
    private sealed class UnlockedDeviceLockStore : IDeviceLockStore
    {
        public Task SetAsync(long permAuthKeyId, int periodSeconds) => Task.CompletedTask;
        public Task<bool> IsLockedAsync(long permAuthKeyId) => Task.FromResult(false);
    }

    /// <summary>Captures every published command's type name and target aggregate id.</summary>
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
