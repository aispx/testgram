using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 4: Web-push token validation yields the correct outcome
//
// For any JSON token at TokenType == 10:
//   - endpoint + valid base64url keys.auth + valid base64url P-256 keys.p256dh => valid (null)
//   - missing endpoint                                  => 400 WEBPUSH_TOKEN_INVALID
//   - keys.auth missing / not base64url                 => 400 WEBPUSH_AUTH_INVALID
//   - keys.p256dh missing / not a valid P-256 key       => 400 WEBPUSH_KEY_INVALID
//
// Validates: Requirements 1.5, 1.6, 1.7, 1.8
public class Property04_WebPushTokenValidationTests
{
    private static readonly IPushTokenValidator Validator = new PushTokenValidator();

    private const int WebPushTokenType = 10;

    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void WebPush_token_validation_yields_correct_outcome(WebPushTokenCase tokenCase)
    {
        var result = Validator.Validate(WebPushTokenType, tokenCase.Json);

        switch (tokenCase.Kind)
        {
            case WebPushTokenKind.Valid:
                result.ShouldBeNull();
                break;

            case WebPushTokenKind.MissingEndpoint:
                result.ShouldBe(RpcErrors.RpcErrors400.WebpushTokenInvalid);
                break;

            case WebPushTokenKind.MissingAuth:
            case WebPushTokenKind.InvalidAuth:
                result.ShouldBe(RpcErrors.RpcErrors400.WebpushAuthInvalid);
                break;

            case WebPushTokenKind.MissingKey:
            case WebPushTokenKind.InvalidKey:
                result.ShouldBe(RpcErrors.RpcErrors400.WebpushKeyInvalid);
                break;

            default:
                throw new InvalidOperationException($"Unhandled web-push token kind: {tokenCase.Kind}");
        }
    }
}
