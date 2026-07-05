// Feature: push-updates, Property 8: Сигнал устаревшего токена от провайдера удаляет устройство.
//
// For any device, if the sender returns the outcome TokenInvalidated (APNs 410, FCM 404
// UNREGISTERED), the delivery service (PushNotificationEventHandler) publishes an
// UnRegisterDeviceCommand for that device's Token (PushDeviceId.Create(device.Token)); for any other
// outcome (Delivered, TransientFailure) the device is not removed (no UnRegisterDeviceCommand is
// published).
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes: a stubbed
// IPushDispatcher returning a generated PushSendOutcome, a query processor returning exactly one
// device, an offline IPushOnlineFilter, an unlocked IDeviceLockStore, and a capturing ICommandBus.
// It then asserts the biconditional: UnRegisterDeviceCommand is published iff the outcome is
// TokenInvalidated, and (when published) it targets PushDeviceId.Create(device.Token).
//
// Validates: Requirements 3.4

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
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.EventHandlers;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel;
using MyTelegram.Services.Services;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property08_StaleTokenUnregistersTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 8: Сигнал устаревшего токена от провайдера удаляет устройство
    // Validates: Requirements 3.4
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Stale_token_outcome_unregisters_device_iff_token_invalidated(
        DeviceRegistration reg,
        PushSendOutcome outcome)
    {
        // Arrange: a single registered device addressable to its owner. The secret is left null so
        // the encryptor takes the plaintext fallback: this property is about the delivery service's
        // reaction to the dispatcher outcome (Req 3.4), so it must be isolated from the (separately
        // tested, see Property 17) MTProto payload-encryption path.
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
        var dispatcher = new StubDispatcher(outcome);
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

        // A new-message notification addressed to the device owner. UserId != 0 so ResolveUserId
        // returns it directly; no ExcludeAuthKeyId so the device is not skipped.
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

        // Assert: the dispatcher was always invoked exactly once for the single deliverable device.
        dispatcher.Calls.ShouldBe(1, $"outcome={outcome}");

        var unregisterCommands = commandBus.Published
            .Where(c => c.TypeName == "UnRegisterDeviceCommand")
            .ToList();

        if (outcome == PushSendOutcome.TokenInvalidated)
        {
            // The stale-token signal removes the device for its exact Token (Req 3.4).
            unregisterCommands.Count.ShouldBe(1, $"outcome={outcome}");
            unregisterCommands[0].AggregateId.ShouldBe(
                PushDeviceId.Create(device.Token),
                $"unregister must target PushDeviceId.Create(token) for token '{device.Token}'");
        }
        else
        {
            // Delivered / TransientFailure must never remove the device.
            unregisterCommands.ShouldBeEmpty($"outcome={outcome} must not unregister the device");
        }
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Returns the generated outcome for every send and counts invocations.</summary>
    private sealed class StubDispatcher(PushSendOutcome outcome) : IPushDispatcher
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(outcome);
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
