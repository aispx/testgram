// Feature: push-updates, Property 21: Routing by token type.
//
// For any device whose TokenType selects an implemented provider (with that provider enabled), the
// PushDispatcher routes the payload to exactly one sender per the token-type table:
//   * 2  -> FCM      (Requirement 6.1)
//   * 1  -> APNS     (Requirement 6.2)
//   * 9  -> APNS     (APNS VoIP, Requirement 6.2)
//   * 10 -> Web Push (Requirement 6.3)
//
// The property drives the PRODUCTION PushDispatcher with three recording fakes (one per sender
// interface) and an options value whose FCM/APNS/WebPush credentials make each provider Enabled.
// For every generated routed token type it asserts that the matching sender was invoked exactly
// once and the other two senders were never invoked.
//
// Validates: Requirements 6.1, 6.2, 6.3

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

public class Property21_TokenTypeRoutingTests
{
    /// <summary>The provider a routed token type is expected to be dispatched to.</summary>
    private enum ExpectedProvider
    {
        Fcm,
        Apns,
        WebPush
    }

    /// <summary>A routed token type paired with the single provider it must reach.</summary>
    public sealed record RoutingCase(int TokenType)
    {
        public override string ToString() => $"TokenType={TokenType}";
    }

    private static class RoutingArbitraries
    {
        // Only the implemented/routed token types (2=FCM, 1/9=APNS, 10=WebPush). The
        // unsupported-types path is covered by a separate property (Property 22).
        public static Arbitrary<RoutingCase> RoutingCase() =>
            Arb.From(
                from tokenType in Gen.Elements(
                    PushTokenType.Fcm, PushTokenType.Apns, PushTokenType.ApnsVoip, PushTokenType.WebPush)
                select new RoutingCase(tokenType));
    }

    // Property 21: Routing by token type
    // Validates: Requirements 6.1, 6.2, 6.3
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(RoutingArbitraries) })]
    public void Dispatcher_routes_payload_to_sender_matching_token_type(RoutingCase routingCase)
    {
        // Arrange: each sender records whether it was invoked (and reports Delivered).
        var fcm = new RecordingSender();
        var apns = new RecordingSender();
        var webPush = new RecordingSender();

        // Credentials chosen so every provider's Enabled computed property is true.
        var options = Options.Create(new MyTelegramMessengerServerOptions
        {
            Push = new PushConfig
            {
                Enabled = true,
                Fcm = new PushConfig.FcmConfig
                {
                    ServiceAccountJson = "{\"type\":\"service_account\"}"
                },
                Apns = new PushConfig.ApnsConfig
                {
                    AuthKeyP8 = "-----BEGIN PRIVATE KEY-----\nMIG\n-----END PRIVATE KEY-----",
                    KeyId = "KEY12345",
                    TeamId = "TEAM12345",
                    BundleId = "com.example.app"
                },
                WebPush = new PushConfig.WebPushConfig
                {
                    VapidPrivateKey = "vapid-private-key",
                    VapidPublicKey = "vapid-public-key",
                    VapidSubject = "mailto:admin@example.com"
                }
            }
        });

        options.Value.Push.Fcm.Enabled.ShouldBeTrue();
        options.Value.Push.Apns.Enabled.ShouldBeTrue();
        options.Value.Push.WebPush.Enabled.ShouldBeTrue();

        var dispatcher = new PushDispatcher(
            new FakeFcmSender(fcm),
            new FakeApnsSender(apns),
            new FakeWebPushSender(webPush),
            options,
            NullLogger<PushDispatcher>.Instance);

        var device = new FakePushDeviceReadModel
        {
            Id = "routing-token",
            Token = "routing-token",
            TokenType = routingCase.TokenType,
            UserId = 1,
            PermAuthKeyId = 1,
            Secret = null
        };

        // Act
        var outcome = dispatcher.SendAsync(device, "cGF5bG9hZA").GetAwaiter().GetResult();

        // Assert: exactly the expected sender was invoked once; the others not at all.
        var expected = routingCase.TokenType switch
        {
            PushTokenType.Fcm => ExpectedProvider.Fcm,
            PushTokenType.Apns => ExpectedProvider.Apns,
            PushTokenType.ApnsVoip => ExpectedProvider.Apns,
            PushTokenType.WebPush => ExpectedProvider.WebPush,
            _ => throw new InvalidOperationException($"unrouted token type {routingCase.TokenType}")
        };

        outcome.ShouldBe(PushSendOutcome.Delivered);

        fcm.Calls.ShouldBe(expected == ExpectedProvider.Fcm ? 1 : 0,
            $"FCM calls for tokenType={routingCase.TokenType}");
        apns.Calls.ShouldBe(expected == ExpectedProvider.Apns ? 1 : 0,
            $"APNS calls for tokenType={routingCase.TokenType}");
        webPush.Calls.ShouldBe(expected == ExpectedProvider.WebPush ? 1 : 0,
            $"WebPush calls for tokenType={routingCase.TokenType}");
    }

    /// <summary>Counts how many times it was invoked and echoes the device it was given.</summary>
    private sealed class RecordingSender
    {
        public int Calls { get; private set; }

        public PushSendOutcome Record(IPushDeviceReadModel device)
        {
            Calls++;
            return PushSendOutcome.Delivered;
        }
    }

    private sealed class FakeFcmSender(RecordingSender recorder) : IPushFcmSender
    {
        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
            => Task.FromResult(recorder.Record(device));
    }

    private sealed class FakeApnsSender(RecordingSender recorder) : IPushApnsSender
    {
        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
            => Task.FromResult(recorder.Record(device));
    }

    private sealed class FakeWebPushSender(RecordingSender recorder) : IPushWebPushSender
    {
        public Task<PushSendOutcome> SendAsync(IPushDeviceReadModel device, string base64Payload)
            => Task.FromResult(recorder.Record(device));
    }
}
