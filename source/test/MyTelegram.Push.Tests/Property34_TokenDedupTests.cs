// Feature: push-updates, Property 34: Дедупликация по уникальному токену.
//
// For any set of recipient devices, the number of actual push sends equals the number of unique
// non-empty Token values among them: the delivery service (PushNotificationEventHandler) sends at
// most one push per unique token (Req 10.3).
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes (the same
// wiring pattern as Property 8): a stubbed IQueryProcessor returning the generated device set
// verbatim, an offline IPushOnlineFilter (so every device is deliverable), an unlocked
// IDeviceLockStore, a capturing ICommandBus, and a StubDispatcher that always reports Delivered and
// records the Token it was asked to send to. It then asserts that the number of sends equals the
// number of distinct non-empty Tokens and that no token was sent to twice.
//
// Each device gets a distinct PermAuthKeyId (so neither the exclude nor the online filter collapses
// two devices) and a null Secret (so the test is isolated from the separately-tested MTProto
// payload-encryption path); the Tokens are left as generated, so duplicates are preserved.
//
// Validates: Requirements 10.3

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

public class Property34_TokenDedupTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 34: Дедупликация по уникальному токену
    // Validates: Requirements 10.3
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Number_of_sends_equals_number_of_unique_non_empty_tokens(DeviceSet deviceSet)
    {
        // Arrange: take the generated device set (built from a tiny token pool, so duplicate Tokens
        // are common) and normalise each device so the ONLY thing that can affect the send count is
        // the Token-dedup logic under test:
        //   * a distinct PermAuthKeyId per device (the exclude/online filters key off PermAuthKeyId),
        //   * a null Secret (isolate from MTProto encryption), and
        //   * the recipient stamped as the owner so routing is irrelevant (the stub returns them all).
        var recipient = deviceSet.RecipientUserId;
        var devices = deviceSet.Devices
            .Select((d, i) => new FakePushDeviceReadModel
            {
                Id = d.Token,
                UserId = recipient,
                PermAuthKeyId = i + 1,
                TokenType = d.TokenType,
                Token = d.Token,
                Secret = null,
                NoMuted = d.NoMuted,
                AppSandbox = d.AppSandbox,
                OtherUids = d.OtherUids
            })
            .ToList();

        var expectedSends = devices
            .Select(d => d.Token)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .Count();

        var queryProcessor = new StubQueryProcessor(devices);
        var dispatcher = new StubDispatcher();
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

        // Act
        handler.HandleEventAsync(eventData).GetAwaiter().GetResult();

        // Assert: exactly one push per unique non-empty token.
        dispatcher.SentTokens.Count.ShouldBe(
            expectedSends,
            $"devices={devices.Count}, tokens=[{string.Join(",", devices.Select(d => d.Token))}]");

        // And the dispatcher was never asked to send to the same token twice.
        dispatcher.SentTokens
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(dispatcher.SentTokens.Count, "no token may be pushed to more than once");
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Reports Delivered for every send and records the device Token it was asked to use.</summary>
    private sealed class StubDispatcher : IPushDispatcher
    {
        public List<string> SentTokens { get; } = new();

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            SentTokens.Add(device.Token);
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
