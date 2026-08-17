// Feature: push-updates, Property 36: Empty credentials mean the provider is disabled.
//
// For any provider configuration, the computed Enabled flag of each provider is TRUE iff the set of
// credentials required by that provider are ALL present (non-blank), and FALSE iff any required
// credential is missing/empty/whitespace. "Empty credentials" therefore means "disabled provider".
//
// The required-credential sets are read directly from the production PushConfig Enabled getters
// (MyTelegram.Messenger.MyTelegramMessengerServerOptions):
//   Fcm.Enabled     <=> ServiceAccountJson is non-blank
//   Apns.Enabled    <=> AuthKeyP8 && KeyId && TeamId are ALL non-blank   (BundleId is NOT required)
//   WebPush.Enabled <=> VapidPrivateKey && VapidPublicKey are BOTH non-blank (VapidSubject NOT required)
//
// The generator below independently varies EACH credential across the full "blank" space
// (null / "" / " " / "\t" / "\n" / " \t \n ") and several non-blank shapes, and also randomises the
// non-required fields (BundleId, VapidSubject, master Enabled flag, timeouts) to prove they never
// influence the Enabled flags. With MaxTest = 100 both directions of the "iff" are exercised.
//
// Validates: Requirements 11.4

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property36_EmptyCredentialsDisabledTests
{
    /// <summary>
    /// A fully randomised <see cref="PushConfig"/> where every individual credential (required and
    /// non-required) is independently present (non-blank) or blank (null/empty/whitespace).
    /// </summary>
    public sealed record EmptyCredentialsCase(PushConfig Config)
    {
        public override string ToString() =>
            $"PushConfig(master={Config.Enabled}, " +
            $"fcm[sa={Show(Config.Fcm.ServiceAccountJson)}], " +
            $"apns[p8={Show(Config.Apns.AuthKeyP8)}, kid={Show(Config.Apns.KeyId)}, " +
            $"team={Show(Config.Apns.TeamId)}, bundle={Show(Config.Apns.BundleId)}], " +
            $"web[priv={Show(Config.WebPush.VapidPrivateKey)}, pub={Show(Config.WebPush.VapidPublicKey)}, " +
            $"subj={Show(Config.WebPush.VapidSubject)}])";

        private static string Show(string? s) =>
            s is null ? "<null>" : string.IsNullOrWhiteSpace(s) ? $"<blank:{s.Length}>" : "<set>";
    }

    private static class Arbitraries
    {
        // The full "blank" space the requirement cares about: missing OR empty OR whitespace.
        private static readonly string?[] BlankValues = { null, "", " ", "\t", "\n", " \t \n " };

        // A handful of representative non-blank shapes (incl. ones containing internal whitespace).
        private static readonly string[] NonBlankValues =
        {
            "x",
            "{\"type\":\"service_account\"}",
            "-----BEGIN PRIVATE KEY-----\nMOCK\n-----END PRIVATE KEY-----",
            "ABC123DEFG",
            "TEAM123456",
            "com.example.app",
            "mailto:admin@example.com",
            "cHJpdmF0ZS1rZXk",
            "  has internal spaces  but is non blank",
        };

        // Roughly balanced 50/50 between blank and non-blank so both directions of the iff get hit.
        private static Gen<string?> CredentialGen() =>
            Gen.OneOf(
                Gen.Elements(BlankValues),
                Gen.Elements<string?>(NonBlankValues));

        public static Arbitrary<EmptyCredentialsCase> Case()
        {
            var gen =
                from master in Arb.Generate<bool>()
                from serviceAccountJson in CredentialGen()
                from authKeyP8 in CredentialGen()
                from keyId in CredentialGen()
                from teamId in CredentialGen()
                from bundleId in CredentialGen()
                from vapidPrivate in CredentialGen()
                from vapidPublic in CredentialGen()
                from vapidSubject in CredentialGen()
                select new EmptyCredentialsCase(new PushConfig
                {
                    Enabled = master,
                    Fcm = new PushConfig.FcmConfig
                    {
                        ServiceAccountJson = serviceAccountJson
                    },
                    Apns = new PushConfig.ApnsConfig
                    {
                        AuthKeyP8 = authKeyP8,
                        KeyId = keyId,
                        TeamId = teamId,
                        BundleId = bundleId
                    },
                    WebPush = new PushConfig.WebPushConfig
                    {
                        VapidPrivateKey = vapidPrivate,
                        VapidPublicKey = vapidPublic,
                        VapidSubject = vapidSubject
                    }
                });

            return Arb.From(gen);
        }
    }

    // Property 36: Empty credentials mean the provider is disabled
    // Validates: Requirements 11.4
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Arbitraries) })]
    public void Empty_credentials_mean_disabled_provider(EmptyCredentialsCase c)
    {
        var cfg = c.Config;

        var fcmCredsPresent = !string.IsNullOrWhiteSpace(cfg.Fcm.ServiceAccountJson);
        var apnsCredsPresent = !string.IsNullOrWhiteSpace(cfg.Apns.AuthKeyP8)
                               && !string.IsNullOrWhiteSpace(cfg.Apns.KeyId)
                               && !string.IsNullOrWhiteSpace(cfg.Apns.TeamId);
        var webCredsPresent = !string.IsNullOrWhiteSpace(cfg.WebPush.VapidPrivateKey)
                              && !string.IsNullOrWhiteSpace(cfg.WebPush.VapidPublicKey);

        // The "iff": Enabled is true exactly when all required credentials are non-blank.
        cfg.Fcm.Enabled.ShouldBe(fcmCredsPresent, $"FCM Enabled mismatch for {c}");
        cfg.Apns.Enabled.ShouldBe(apnsCredsPresent, $"APNS Enabled mismatch for {c}");
        cfg.WebPush.Enabled.ShouldBe(webCredsPresent, $"WebPush Enabled mismatch for {c}");

        // Non-required fields must NOT affect enablement: the master switch, APNS BundleId and the
        // WebPush VapidSubject can be anything (blank or set) without changing a provider's Enabled.
        cfg.Apns.Enabled.ShouldBe(
            !string.IsNullOrWhiteSpace(cfg.Apns.AuthKeyP8)
            && !string.IsNullOrWhiteSpace(cfg.Apns.KeyId)
            && !string.IsNullOrWhiteSpace(cfg.Apns.TeamId),
            "APNS BundleId must not influence Enabled");
        cfg.WebPush.Enabled.ShouldBe(
            !string.IsNullOrWhiteSpace(cfg.WebPush.VapidPrivateKey)
            && !string.IsNullOrWhiteSpace(cfg.WebPush.VapidPublicKey),
            "WebPush VapidSubject must not influence Enabled");
    }
}
