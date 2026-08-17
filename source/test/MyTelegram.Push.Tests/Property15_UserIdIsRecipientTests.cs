// Feature: push-updates, Property 15: user_id equals the recipient account.
//
// For any recipient account recipientUserId, the final payload that the delivery service
// (PushNotificationEventHandler) hands to a device must carry user_id == recipientUserId — the
// recipient account is always stamped into the payload's user_id, regardless of what user_id (if
// any) the source PushData carried. This is what lets a multi-account client route the
// notification to the correct local account.
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes (the exact
// wiring used by Property 8): a stubbed IPushDispatcher that captures the base64url payload, a query
// processor returning exactly one device owned by the recipient, an offline IPushOnlineFilter, an
// unlocked IDeviceLockStore and a capturing ICommandBus. The incoming PushData deliberately carries
// user_id == 0 so the recipient is resolved from the User peer and then stamped; the device Secret
// is null so the encryptor takes the plaintext fallback and the test can decode the JSON payload and
// read "user_id" directly (the MTProto-encrypted path is covered separately by Property 17).
//
// Validates: Requirements 4.7, 10.1

using System.Text;
using System.Text.Json;
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

public class Property15_UserIdIsRecipientTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 15: user_id equals the recipient account
    // Validates: Requirements 4.7, 10.1
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Final_payload_user_id_equals_recipient_account(
        DeviceRegistration reg,
        PushData payloadTemplate)
    {
        // The recipient account: a non-zero id (PooledUserId is 1..20, so always > 0).
        var recipientUserId = reg.UserId;

        // A single device owned by the recipient. Secret = null so the encryptor returns base64url
        // of the plaintext JSON, letting the test decode and read "user_id" without the MTProto
        // crypto path (isolated, exactly as Property 8 does).
        var device = new FakePushDeviceReadModel
        {
            Id = reg.Token,
            UserId = recipientUserId,
            PermAuthKeyId = reg.PermAuthKeyId,
            TokenType = reg.TokenType,
            Token = reg.Token,
            Secret = null,
            NoMuted = reg.NoMuted,
            AppSandbox = reg.AppSandbox,
            OtherUids = reg.OtherUids
        };

        var queryProcessor = new StubQueryProcessor(new IPushDeviceReadModel[] { device });
        var dispatcher = new CapturingDispatcher();
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

        // The source payload carries user_id == 0 on purpose: the recipient must be resolved from
        // the User peer and then stamped into user_id. Reuse a generated PushData for the loc_key /
        // loc_args / custom variety, but null out its UserId.
        var pushData = payloadTemplate with { UserId = 0 };

        var eventData = new LayeredPushMessageCreatedIntegrationEvent(
            PeerType.User,
            recipientUserId,
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

        // The single deliverable device must have received exactly one push.
        dispatcher.Payloads.Count.ShouldBe(1, $"recipient={recipientUserId}");

        // Decode the base64url plaintext JSON the dispatcher received and read user_id.
        var json = Encoding.UTF8.GetString(Base64UrlReference.Decode(dispatcher.Payloads[0]));
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.TryGetProperty("user_id", out var userIdElement)
            .ShouldBeTrue($"payload must stamp user_id; json={json}");

        userIdElement.GetInt64().ShouldBe(
            recipientUserId,
            $"final payload user_id must equal recipient account {recipientUserId}; json={json}");
    }

    /// <summary>Returns a fixed device collection for any query (the handler only issues
    /// <c>GetPushDevicesForRecipientQuery</c>).</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Captures the base64url payload handed to it and reports successful delivery.</summary>
    private sealed class CapturingDispatcher : IPushDispatcher
    {
        public List<string> Payloads { get; } = new();

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Payloads.Add(base64Payload);
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
