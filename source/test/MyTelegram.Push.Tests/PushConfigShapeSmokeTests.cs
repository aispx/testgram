// Feature: push-updates, SMOKE 11.2: PushConfig exposes the Fcm/Apns/WebPush provider blocks
// with the required string fields, so an operator can configure every supported provider.
//
// SMOKE 11.2 — PushConfig (MyTelegram.Messenger) must surface, for each supported push provider,
// a nested configuration block carrying the credentials documented in Requirement 11.2:
//   Fcm.ServiceAccountJson
//   Apns.AuthKeyP8, Apns.KeyId, Apns.TeamId, Apns.BundleId
//   WebPush.VapidPrivateKey, WebPush.VapidPublicKey, WebPush.VapidSubject
//
// This is a focused smoke test: it (a) confirms the nested blocks exist and are non-null by default,
// (b) round-trips each required field by writing then reading it back, and (c) asserts via reflection
// that each property exists as a readable/writable string of the expected type.
//
// Validates: Requirements 11.2

using System.Reflection;
using MyTelegram.Messenger;
using Shouldly;
using Xunit;

namespace MyTelegram.Push.Tests;

public class PushConfigShapeSmokeTests
{
    [Fact]
    public void PushConfig_exposes_nonnull_provider_blocks_by_default()
    {
        var cfg = new PushConfig();

        cfg.Fcm.ShouldNotBeNull();
        cfg.Apns.ShouldNotBeNull();
        cfg.WebPush.ShouldNotBeNull();
    }

    [Fact]
    public void Fcm_block_round_trips_required_fields()
    {
        var cfg = new PushConfig();

        cfg.Fcm.ServiceAccountJson = "{\"type\":\"service_account\"}";

        cfg.Fcm.ServiceAccountJson.ShouldBe("{\"type\":\"service_account\"}");
    }

    [Fact]
    public void Apns_block_round_trips_required_fields()
    {
        var cfg = new PushConfig();

        cfg.Apns.AuthKeyP8 = "-----BEGIN PRIVATE KEY-----\nMOCK\n-----END PRIVATE KEY-----";
        cfg.Apns.KeyId = "KEY1234567";
        cfg.Apns.TeamId = "TEAM123456";
        cfg.Apns.BundleId = "com.example.app";

        cfg.Apns.AuthKeyP8.ShouldBe("-----BEGIN PRIVATE KEY-----\nMOCK\n-----END PRIVATE KEY-----");
        cfg.Apns.KeyId.ShouldBe("KEY1234567");
        cfg.Apns.TeamId.ShouldBe("TEAM123456");
        cfg.Apns.BundleId.ShouldBe("com.example.app");
    }

    [Fact]
    public void WebPush_block_round_trips_required_fields()
    {
        var cfg = new PushConfig();

        cfg.WebPush.VapidPrivateKey = "cHJpdmF0ZS1rZXk";
        cfg.WebPush.VapidPublicKey = "cHVibGljLWtleQ";
        cfg.WebPush.VapidSubject = "mailto:admin@example.com";

        cfg.WebPush.VapidPrivateKey.ShouldBe("cHJpdmF0ZS1rZXk");
        cfg.WebPush.VapidPublicKey.ShouldBe("cHVibGljLWtleQ");
        cfg.WebPush.VapidSubject.ShouldBe("mailto:admin@example.com");
    }

    [Theory]
    // PushConfig -> provider block properties
    [InlineData(typeof(PushConfig), "Fcm")]
    [InlineData(typeof(PushConfig), "Apns")]
    [InlineData(typeof(PushConfig), "WebPush")]
    public void Provider_block_property_exists_and_is_readable(Type owner, string blockName)
    {
        var prop = owner.GetProperty(blockName, BindingFlags.Public | BindingFlags.Instance);
        prop.ShouldNotBeNull($"{owner.Name} must expose a '{blockName}' provider block");
        prop!.CanRead.ShouldBeTrue($"{blockName} must be readable");
    }

    [Theory]
    // Fcm required fields
    [InlineData(typeof(PushConfig.FcmConfig), "ServiceAccountJson")]
    // Apns required fields
    [InlineData(typeof(PushConfig.ApnsConfig), "AuthKeyP8")]
    [InlineData(typeof(PushConfig.ApnsConfig), "KeyId")]
    [InlineData(typeof(PushConfig.ApnsConfig), "TeamId")]
    [InlineData(typeof(PushConfig.ApnsConfig), "BundleId")]
    // WebPush required fields
    [InlineData(typeof(PushConfig.WebPushConfig), "VapidPrivateKey")]
    [InlineData(typeof(PushConfig.WebPushConfig), "VapidPublicKey")]
    [InlineData(typeof(PushConfig.WebPushConfig), "VapidSubject")]
    public void Required_field_exists_as_readwrite_string(Type owner, string fieldName)
    {
        var prop = owner.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);

        prop.ShouldNotBeNull($"{owner.Name} must expose a '{fieldName}' field");
        prop!.PropertyType.ShouldBe(typeof(string), $"{owner.Name}.{fieldName} must be a string");
        prop.CanRead.ShouldBeTrue($"{owner.Name}.{fieldName} must be readable");
        prop.CanWrite.ShouldBeTrue($"{owner.Name}.{fieldName} must be writable");
    }
}
