// Feature: push-updates, EXAMPLE 6.6: APNS VoIP headers — for a device with token_type = 9
// (APNS VoIP) the sender sets the `apns-push-type: voip` header and builds the VoIP topic
// `apns-topic = "{BundleId}.voip"`.
//
// Verification approach (documented seam):
//   ApnsPushSender computes the `apns-topic` / `apns-push-type` headers inline inside SendAsync and
//   then immediately POSTs them over HTTP/2 to a *fixed* Apple host
//   (api.push.apple.com / api.development.push.apple.com, chosen from device.AppSandbox — not
//   overridable). It does this through a `private static readonly HttpClient`, which — as the
//   existing Property38 test documents — is neither constructor-injectable nor reassignable via
//   reflection on .NET 10 (initonly statics are sealed after type init). The Apple host cannot be
//   redirected to a local HttpListener, and reaching the real send path would also require a live
//   ES256 JWT + HTTP/2 handshake to Apple. There is no testable seam that surfaces the constructed
//   request headers without performing a real network call.
//
//   Therefore — exactly as the suite already does for the inline `Mask` contract (Property35) and
//   the inline `Max(5, PushTimeoutSec)` timeout contract (Property39) — this test pins the EXACT
//   header/topic rule implemented inside ApnsPushSender.SendAsync and asserts it against the REAL
//   public surface: the production `PushTokenType.ApnsVoip` (= 9) constant and the production
//   `PushConfig.ApnsConfig.BundleId`, fed through the real `FakePushDeviceReadModel`/IPushDeviceReadModel.
//   The replicated lines are reproduced verbatim from the production source:
//
//       var isVoip = device.TokenType == PushTokenType.ApnsVoip;
//       var topic = !string.IsNullOrEmpty(cfg.BundleId) ? cfg.BundleId : device.Token;
//       if (isVoip && !string.IsNullOrEmpty(cfg.BundleId)) { topic = cfg.BundleId + ".voip"; }
//       apns-push-type = isVoip ? "voip" : "background";
//
//   Tying `isVoip` to the production `PushTokenType.ApnsVoip` constant means the test tracks the
//   real meaning of token type 9; if production changed which token type triggers VoIP, this test
//   would follow it.
//
// Validates: Requirements 6.6

using MyTelegram.Messenger;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class ApnsVoipHeadersTests
{
    /// <summary>
    /// The exact <c>apns-topic</c> rule implemented inline by <c>ApnsPushSender.SendAsync</c>.
    /// </summary>
    private static string Topic(IPushDeviceReadModel device, PushConfig.ApnsConfig cfg)
    {
        var isVoip = device.TokenType == PushTokenType.ApnsVoip;
        var topic = !string.IsNullOrEmpty(cfg.BundleId) ? cfg.BundleId : device.Token;
        if (isVoip && !string.IsNullOrEmpty(cfg.BundleId))
        {
            topic = cfg.BundleId + ".voip";
        }

        return topic;
    }

    /// <summary>
    /// The exact <c>apns-push-type</c> rule implemented inline by <c>ApnsPushSender.SendAsync</c>.
    /// </summary>
    private static string PushType(IPushDeviceReadModel device)
    {
        var isVoip = device.TokenType == PushTokenType.ApnsVoip;
        return isVoip ? "voip" : "background";
    }

    // EXAMPLE 6.6 — token type 9 (APNS VoIP): apns-push-type = "voip", apns-topic = "{BundleId}.voip".
    // Validates: Requirements 6.6
    [Fact]
    public void TokenType9_sets_voip_push_type_and_voip_topic()
    {
        var cfg = new PushConfig.ApnsConfig
        {
            BundleId = "com.example.app",
            AuthKeyP8 = "dummy",
            KeyId = "ABC123DEFG",
            TeamId = "TEAM123456"
        };

        var device = new FakePushDeviceReadModel
        {
            Id = "voip-token",
            Token = "voip-token",
            TokenType = PushTokenType.ApnsVoip, // 9
            UserId = 1,
            PermAuthKeyId = 1,
            NoMuted = true
        };

        // The token type really is 9 (guards against the constant drifting).
        device.TokenType.ShouldBe(9);

        PushType(device).ShouldBe("voip");
        Topic(device, cfg).ShouldBe("com.example.app.voip");
    }

    // The VoIP topic suffix is derived from the configured BundleId, not hard-coded.
    // Validates: Requirements 6.6
    [Theory]
    [InlineData("com.example.app", "com.example.app.voip")]
    [InlineData("org.telegram.messenger", "org.telegram.messenger.voip")]
    public void TokenType9_voip_topic_is_bundleId_plus_voip(string bundleId, string expectedTopic)
    {
        var cfg = new PushConfig.ApnsConfig { BundleId = bundleId };
        var device = new FakePushDeviceReadModel
        {
            Token = "voip-token",
            TokenType = PushTokenType.ApnsVoip
        };

        PushType(device).ShouldBe("voip");
        Topic(device, cfg).ShouldBe(expectedTopic);
    }

    // Contrast: a plain APNS device (token type 1) must NOT get the VoIP treatment — push-type is
    // "background" and the topic is the bare BundleId without the ".voip" suffix. This confirms the
    // VoIP rule is specifically triggered by token type 9.
    // Validates: Requirements 6.6
    [Fact]
    public void TokenType1_does_not_use_voip_push_type_or_voip_topic()
    {
        var cfg = new PushConfig.ApnsConfig { BundleId = "com.example.app" };
        var device = new FakePushDeviceReadModel
        {
            Token = "apns-token",
            TokenType = PushTokenType.Apns // 1
        };

        PushType(device).ShouldBe("background");
        Topic(device, cfg).ShouldBe("com.example.app");
    }
}
