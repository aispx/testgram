// Feature: push-updates, Property 23: Выключенный провайдер никогда не вызывается.
//
// For any device, if the master flag Push.Enabled is false OR the Enabled flag of the provider
// required by the device's TokenType is false, no provider sender is ever invoked (and no
// exception is thrown).
//
// The provider Enabled flags are *derived from credential presence* (see PushConfig):
//   Fcm.Enabled     <=> ServiceAccountJson is non-blank
//   Apns.Enabled    <=> AuthKeyP8 && KeyId && TeamId are all non-blank
//   WebPush.Enabled <=> VapidPrivateKey && VapidPublicKey are non-blank
// so "empty credentials" == "disabled provider". The PushGen.ProviderConfig generator randomises
// both the master flag and each provider's credentials, so every run varies which provider(s) are
// disabled.
//
// The property exercises the two independent guards that together realise Property 23:
//   Phase A (Req 6.5) drives the PRODUCTION PushDispatcher with fake counting senders and asserts
//     that a provider sender is invoked ONLY for the device's required provider AND ONLY when that
//     provider's Enabled flag is true. A disabled (blank-credential) provider is never called.
//   Phase B (Req 11.1) drives the PRODUCTION PushNotificationEventHandler (the delivery service)
//     with the master flag Push.Enabled = false — even with every provider fully configured — and
//     asserts that the dispatcher, and therefore every sender, is never reached.
//
// Validates: Requirements 6.5, 11.1

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

public class Property23_DisabledProviderTests
{
    private static readonly IAuthKeyIdHelper AuthKeyIdHelper = new AuthKeyIdHelper();
    private static readonly IMtpHelper MtpHelper = new MtpHelper(new AesHelper());

    // Property 23: Выключенный провайдер никогда не вызывается
    // Validates: Requirements 6.5, 11.1
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Disabled_provider_is_never_invoked(
        DeviceRegistration reg,
        ProviderConfigCase configCase)
    {
        // ---- Phase A (Req 6.5): the dispatcher never calls a disabled provider --------------
        var cfg = configCase.Config;

        // A device whose TokenType is the generated (supported) token type. The secret is irrelevant
        // here: this property is about provider enablement, not payload encryption.
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

        var fcm = new CountingFcmSender();
        var apns = new CountingApnsSender();
        var webPush = new CountingWebPushSender();

        var dispatcher = new PushDispatcher(
            fcm,
            apns,
            webPush,
            Options.Create(new MyTelegramMessengerServerOptions { Push = cfg }),
            NullLogger<PushDispatcher>.Instance);

        // The dispatcher must complete without throwing for any token type / config (Req 6.4/6.5).
        var outcome = Should.NotThrow(() =>
            dispatcher.SendAsync(device, "payload").GetAwaiter().GetResult());

        // Which provider (if any) the device's TokenType requires, and whether it is enabled.
        var expectFcm = device.TokenType == PushTokenType.Fcm && cfg.Fcm.Enabled;
        var expectApns =
            device.TokenType is PushTokenType.Apns or PushTokenType.ApnsVoip && cfg.Apns.Enabled;
        var expectWebPush = device.TokenType == PushTokenType.WebPush && cfg.WebPush.Enabled;

        // A sender is invoked iff it is the required provider for this TokenType AND it is enabled.
        // In particular, when the required provider's Enabled flag is false (blank credentials), the
        // corresponding sender is NOT called — and the unrelated senders are never called either.
        fcm.Calls.ShouldBe(expectFcm ? 1 : 0,
            $"FCM sender call mismatch for {configCase}, tokenType={device.TokenType}");
        apns.Calls.ShouldBe(expectApns ? 1 : 0,
            $"APNS sender call mismatch for {configCase}, tokenType={device.TokenType}");
        webPush.Calls.ShouldBe(expectWebPush ? 1 : 0,
            $"WebPush sender call mismatch for {configCase}, tokenType={device.TokenType}");

        // When no provider is required/enabled the dispatcher still succeeds (Delivered fall-through).
        if (!expectFcm && !expectApns && !expectWebPush)
        {
            outcome.ShouldBe(PushSendOutcome.Delivered, $"disabled/unsupported provider for {configCase}");
        }

        // ---- Phase B (Req 11.1): master flag off => no provider is ever reached --------------
        // A config with EVERY provider fully credentialed (so every provider Enabled flag is true)
        // but the master switch OFF. The delivery service must short-circuit before dispatching.
        var fcmB = new CountingFcmSender();
        var apnsB = new CountingApnsSender();
        var webPushB = new CountingWebPushSender();
        var masterOffConfig = FullyCredentialedConfig(masterEnabled: false);
        masterOffConfig.Fcm.Enabled.ShouldBeTrue();
        masterOffConfig.Apns.Enabled.ShouldBeTrue();
        masterOffConfig.WebPush.Enabled.ShouldBeTrue();

        var realDispatcher = new PushDispatcher(
            fcmB,
            apnsB,
            webPushB,
            Options.Create(new MyTelegramMessengerServerOptions { Push = masterOffConfig }),
            NullLogger<PushDispatcher>.Instance);

        var recipientUserId = reg.UserId == 0 ? 1 : reg.UserId;
        var ownedDevice = new FakePushDeviceReadModel
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

        var handler = new PushNotificationEventHandler(
            new StubQueryProcessor(new IPushDeviceReadModel[] { ownedDevice }),
            realDispatcher,
            new OfflineFilter(),
            new UnlockedDeviceLockStore(),
            new NoopCommandBus(),
            MtpHelper,
            AuthKeyIdHelper,
            Options.Create(new MyTelegramMessengerServerOptions { Push = masterOffConfig }),
            NullLogger<PushNotificationEventHandler>.Instance);

        var pushData = new PushData(
            PushNotificationTypes.MessageText,
            new[] { "Alice", "hello" },
            recipientUserId,
            null,
            "default");

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

        Should.NotThrow(() => handler.HandleEventAsync(eventData).GetAwaiter().GetResult());

        // Master flag off => not a single provider sender was invoked (Req 11.1).
        fcmB.Calls.ShouldBe(0, "master flag off must not reach FCM");
        apnsB.Calls.ShouldBe(0, "master flag off must not reach APNS");
        webPushB.Calls.ShouldBe(0, "master flag off must not reach WebPush");
    }

    /// <summary>A PushConfig with every provider's credentials populated (so each Enabled flag is
    /// true), with the master switch set by <paramref name="masterEnabled"/>.</summary>
    private static PushConfig FullyCredentialedConfig(bool masterEnabled)
    {
        var cfg = new PushConfig { Enabled = masterEnabled };
        cfg.Fcm.ServiceAccountJson = "{\"type\":\"service_account\"}";
        cfg.Apns.AuthKeyP8 = "-----BEGIN PRIVATE KEY-----\nMOCK\n-----END PRIVATE KEY-----";
        cfg.Apns.KeyId = "ABC123DEFG";
        cfg.Apns.TeamId = "TEAM123456";
        cfg.Apns.BundleId = "com.example.app";
        cfg.WebPush.VapidPrivateKey = "cHJpdmF0ZS1rZXk";
        cfg.WebPush.VapidPublicKey = "cHVibGljLWtleQ";
        cfg.WebPush.VapidSubject = "mailto:admin@example.com";
        return cfg;
    }

    private sealed class CountingFcmSender : IPushFcmSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    private sealed class CountingApnsSender : IPushApnsSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    private sealed class CountingWebPushSender : IPushWebPushSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Returns a fixed device collection for any query.</summary>
    private sealed class StubQueryProcessor(IReadOnlyCollection<IPushDeviceReadModel> devices) : IQueryProcessor
    {
        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
            => Task.FromResult((TResult)(object)devices);
    }

    /// <summary>Always reports the device offline so delivery would proceed (if not short-circuited).</summary>
    private sealed class OfflineFilter : IPushOnlineFilter
    {
        public Task<bool> IsOnlineAsync(long permAuthKeyId) => Task.FromResult(false);
        public Task MarkOnlineAsync(long permAuthKeyId) => Task.CompletedTask;
    }

    /// <summary>Always reports the device unlocked.</summary>
    private sealed class UnlockedDeviceLockStore : IDeviceLockStore
    {
        public Task SetAsync(long permAuthKeyId, int periodSeconds) => Task.CompletedTask;
        public Task<bool> IsLockedAsync(long permAuthKeyId) => Task.FromResult(false);
    }

    /// <summary>Accepts and ignores any published command.</summary>
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
