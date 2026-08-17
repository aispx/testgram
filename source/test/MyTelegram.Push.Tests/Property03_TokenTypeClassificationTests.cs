using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

/// <summary>
/// Feature: push-updates, Property 3: Token type validity classification.
/// <para>
/// For any integer <c>TokenType</c>, <see cref="PushTokenValidator"/> returns
/// <c>400 TOKEN_TYPE_INVALID</c> if and only if the value is NOT in the supported set
/// {1,2,3,5,6,7,8,9,10,11,12,13}.
/// </para>
/// </summary>
public class Property03_TokenTypeClassificationTests
{
    private const int AndroidInternalPushTokenType = 7;

    private static readonly IPushTokenValidator Validator = new PushTokenValidator();

    [Fact]
    public void Android_internal_push_token_type_7_is_accepted()
    {
        // Telegram Android native tgnet registers an internal push connection with token_type=7
        // and the decimal pushSessionId as token. Rejecting it makes the client retry repeatedly.
        Validator.Validate(AndroidInternalPushTokenType, "2325021865186292874").ShouldBeNull();
    }

    /// <summary>
    /// Pairs a (supported or unsupported) token type with a non-empty token. For type 10 a valid
    /// web-push JSON token is used so the type-classification result is isolated from the WEBPUSH_*
    /// checks; the token is always non-empty so the TOKEN_EMPTY branch never fires.
    /// </summary>
    private static readonly Gen<(int TokenType, string Token)> TokenTypeCaseGen =
        from tokenType in PushGen.AnyTokenType
        from token in tokenType == 10 ? PushGen.ValidWebPushTokenJson : PushGen.NonEmptyToken
        select (tokenType, token);

    // Feature: push-updates, Property 3: Token type validity classification
    // Validates: Requirements 1.3
    [Property(MaxTest = 20)]
    public Property TokenType_is_classified_invalid_iff_outside_supported_set()
    {
        return Prop.ForAll(Arb.From(TokenTypeCaseGen), tc =>
        {
            var result = Validator.Validate(tc.TokenType, tc.Token);

            var classifiedAsTypeInvalid = result == RpcErrors.RpcErrors400.TokenTypeInvalid;
            var expectedTypeInvalid = !PushTokenTypes.IsSupported(tc.TokenType);

            return (classifiedAsTypeInvalid == expectedTypeInvalid)
                .Label($"tokenType={tc.TokenType}, supported={PushTokenTypes.IsSupported(tc.TokenType)}, " +
                       $"result={(result is { } e ? e.Message : "null")}");
        });
    }
}
