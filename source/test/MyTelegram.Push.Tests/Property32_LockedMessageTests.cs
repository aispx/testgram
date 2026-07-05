// Feature: push-updates, Property 32: Блокировка скрывает текст сообщения.
//
// For any new-message notification, if the device's PermAuthKeyId has an active lock, the
// transformed payload (as actually sent to the dispatcher) has loc_key == LOCKED_MESSAGE and
// contains no message text in loc_args; when the lock is NOT active, the original new-message
// loc_key (MESSAGE_TEXT) and its loc_args (sender + body) are preserved.
//
// The property drives the PRODUCTION PushNotificationEventHandler with hand-rolled fakes (reusing
// the wiring pattern from Property08): a query processor returning exactly one device, an offline
// IPushOnlineFilter, a capturing ICommandBus, and — the crux of this property — an IDeviceLockStore
// fake whose IsLockedAsync returns a generated bool. The device Secret is left null so the
// encryptor takes the base64url-of-plaintext-JSON fallback; the stub dispatcher captures that wire
// payload, which the test decodes (Base64UrlReference) and parses as JSON to inspect loc_key /
// loc_args exactly as an official client would receive them.
//
// Validates: Requirements 9.2

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

public class Property32_LockedMessageTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 32: Блокировка скрывает текст сообщения
    // Validates: Requirements 9.2
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Lock_hides_message_text_iff_device_locked(
        DeviceRegistration reg,
        bool locked,
        NonEmptyString senderName,
        NonEmptyString messageBody)
    {
        // Arrange: a single registered device addressable to its owner. Secret == null routes the
        // encryptor through the plaintext-JSON base64url fallback (Req 5.4) so this property stays
        // isolated from the MTProto encryption path (Property 17) and can read loc_key/loc_args
        // straight off the wire.
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
            new ToggleableDeviceLockStore(locked),
            commandBus,
            MtpHelper,
            AuthKeyIdHelper,
            options,
            NullLogger<PushNotificationEventHandler>.Instance);

        // A new-message notification (loc_key MESSAGE_TEXT) carrying sender + body in loc_args,
        // addressed to the device owner. UserId != 0 so ResolveUserId returns it directly; no
        // ExcludeAuthKeyId so the device is not skipped.
        var sender = senderName.Get;
        var body = messageBody.Get;
        var pushData = new PushData(
            PushNotificationTypes.MessageText,
            new[] { sender, body },
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

        // The deliverable device always receives exactly one push.
        dispatcher.Payloads.Count.ShouldBe(1, $"locked={locked}");

        // Decode the wire payload exactly as a client would: base64url -> UTF-8 JSON.
        var json = Encoding.UTF8.GetString(Base64UrlReference.Decode(dispatcher.Payloads[0]));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var locKey = root.GetProperty("loc_key").GetString();
        var locArgs = ReadLocArgs(root);

        if (locked)
        {
            // Req 9.2: a locked device gets LOCKED_MESSAGE with no message text in loc_args.
            locKey.ShouldBe(PushNotificationTypes.LockedMessage, $"json={json}");
            // "no message text in loc_args" — the rewrite empties loc_args, which is omitted from
            // the JSON entirely, so neither the sender name nor the body can reach the device.
            locArgs.ShouldBeEmpty($"locked payload must carry no loc_args, json={json}");
            locArgs.ShouldNotContain(sender, $"json={json}");
            locArgs.ShouldNotContain(body, $"json={json}");
        }
        else
        {
            // Unlocked: the original new-message payload is delivered untouched.
            locKey.ShouldBe(PushNotificationTypes.MessageText, $"json={json}");
            locArgs.ShouldBe(new[] { sender, body }, $"json={json}");
        }
    }

    private static IReadOnlyList<string> ReadLocArgs(JsonElement root)
    {
        if (!root.TryGetProperty("loc_args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var element in args.EnumerateArray())
        {
            list.Add(element.GetString() ?? string.Empty);
        }

        return list;
    }

    /// <summary>Returns a fixed device collection for any query.</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Captures every base64url wire payload handed to the dispatcher; always succeeds.</summary>
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

    /// <summary>Reports the configured (generated) lock state for any auth key.</summary>
    private sealed class ToggleableDeviceLockStore(bool locked) : IDeviceLockStore
    {
        public Task SetAsync(long permAuthKeyId, int periodSeconds) => Task.CompletedTask;
        public Task<bool> IsLockedAsync(long permAuthKeyId) => Task.FromResult(locked);
    }

    /// <summary>Captures every published command (none are expected for a Delivered outcome).</summary>
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
