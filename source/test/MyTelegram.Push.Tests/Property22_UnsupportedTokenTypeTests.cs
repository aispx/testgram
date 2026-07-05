// Feature: push-updates, Property 22: Неподдерживаемые типы токенов пропускаются без исключения.
//
// For any TokenType without an implemented sender (3 MPNS, 5 Ubuntu, 6 BlackBerry, 7 Android
// internal push, 8 WNS, 11 MPNS VoIP, 12 Tizen, 13 Huawei) the router (PushDispatcher) calls none of the provider senders,
// throws no exception, and completes successfully (returning a no-op Delivered outcome).
//
// The property drives the PRODUCTION PushDispatcher with recording fakes for all three senders
// (FCM / APNS / Web Push) and a configuration where EVERY provider is enabled — so the only reason
// a sender would NOT be invoked is the routing decision under test (Req 6.4), not a disabled
// provider. For every unsupported token type it asserts: SendAsync does not throw, returns
// PushSendOutcome.Delivered (the no-op skip), and not one of the fakes recorded a call.
//
// Validates: Requirements 6.4

using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property22_UnsupportedTokenTypeTests
{
    /// <summary>Token types accepted at registration but having NO implemented dispatcher sender.</summary>
    private static readonly int[] UnsupportedByDispatcher =
    {
        PushTokenType.Mpns,
        PushTokenType.Ubuntu,
        PushTokenType.BlackBerry,
        PushTokenType.InternalPush,
        PushTokenType.Wns,
        PushTokenType.MpnsVoip,
        PushTokenType.Tizen,
        PushTokenType.Huawei
    };

    // Property 22: Неподдерживаемые типы токенов пропускаются без исключения
    // Validates: Requirements 6.4
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries), typeof(Property22Arbitraries) })]
    public void Unsupported_token_types_are_skipped_without_exception(
        DeviceRegistration reg,
        UnsupportedDispatcherTokenType tokenType)
    {
        // Arrange: a device identical to a generated registration but stamped with a token type that
        // the dispatcher has no sender for. Reusing the task-1 DeviceRegistration generator keeps the
        // remaining fields (user/auth key/token/flags/other uids) varied.
        var device = new FakePushDeviceReadModel
        {
            Id = reg.Token,
            UserId = reg.UserId,
            PermAuthKeyId = reg.PermAuthKeyId,
            TokenType = tokenType.Value,
            Token = reg.Token,
            Secret = null,
            NoMuted = reg.NoMuted,
            AppSandbox = reg.AppSandbox,
            OtherUids = reg.OtherUids
        };

        var fcm = new RecordingFcmSender();
        var apns = new RecordingApnsSender();
        var webPush = new RecordingWebPushSender();

        // Every provider is enabled, so a sender being skipped can only be the routing decision.
        var options = Options.Create(new MyTelegramMessengerServerOptions { Push = AllProvidersEnabled() });

        var dispatcher = new PushDispatcher(
            fcm,
            apns,
            webPush,
            options,
            NullLogger<PushDispatcher>.Instance);

        // Act: must not throw for any unsupported token type.
        var outcome = Should.NotThrow(() =>
            dispatcher.SendAsync(device, "cGF5bG9hZA").GetAwaiter().GetResult());

        // Assert: completes successfully as a no-op skip and invokes no provider sender.
        outcome.ShouldBe(PushSendOutcome.Delivered, $"tokenType={tokenType.Value}");
        fcm.Calls.ShouldBe(0, $"FCM must not be called for tokenType={tokenType.Value}");
        apns.Calls.ShouldBe(0, $"APNS must not be called for tokenType={tokenType.Value}");
        webPush.Calls.ShouldBe(0, $"WebPush must not be called for tokenType={tokenType.Value}");
    }

    private static PushConfig AllProvidersEnabled()
    {
        var cfg = new PushConfig { Enabled = true };
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

    /// <summary>A token type drawn from {3,5,6,7,8,11,12,13} — accepted at registration, no sender.</summary>
    public sealed record UnsupportedDispatcherTokenType(int Value)
    {
        public override string ToString() => $"UnsupportedTokenType({Value})";
    }

    public static class Property22Arbitraries
    {
        public static Arbitrary<UnsupportedDispatcherTokenType> UnsupportedDispatcherTokenType() =>
            Arb.From(Gen.Elements(UnsupportedByDispatcher).Select(t => new UnsupportedDispatcherTokenType(t)));
    }

    /// <summary>Records every call; the property asserts it is never invoked.</summary>
    private sealed class RecordingFcmSender : IPushFcmSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Records every call; the property asserts it is never invoked.</summary>
    private sealed class RecordingApnsSender : IPushApnsSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }

    /// <summary>Records every call; the property asserts it is never invoked.</summary>
    private sealed class RecordingWebPushSender : IPushWebPushSender
    {
        public int Calls { get; private set; }

        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
        {
            Calls++;
            return Task.FromResult(PushSendOutcome.Delivered);
        }
    }
}
