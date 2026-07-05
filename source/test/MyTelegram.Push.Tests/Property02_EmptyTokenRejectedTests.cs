using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 2: Пустой токен отклоняется как TOKEN_EMPTY.
// For any Token consisting only of empty/whitespace characters (including null and ""), the
// registration validator returns 400 TOKEN_EMPTY and no device is created.
//
// Validates: Requirements 1.2
public class Property02_EmptyTokenRejectedTests
{
    private static readonly IPushTokenValidator Validator = new PushTokenValidator();

    /// <summary>
    /// Empty/whitespace token candidates, explicitly including <c>null</c> and <c>""</c> as required
    /// by the property, plus a spread of whitespace-only strings (spaces, tabs, newlines, mixes).
    /// </summary>
    private static Gen<string?> EmptyOrWhitespaceOrNullToken =>
        Gen.Elements<string?>(null, "", " ", "  ", "\t", "\n", "\r\n", "   \t  ", "\f", "\v", " \t\n ");

    [Property(MaxTest = 20)]
    public Property Empty_or_whitespace_token_is_rejected_as_token_empty()
    {
        // The token type is also varied (any int) to prove the empty-token check is applied first and
        // independently of the token type.
        return Prop.ForAll(
            Arb.From(EmptyOrWhitespaceOrNullToken),
            Arb.From(PushGen.AnyTokenType),
            (token, tokenType) =>
            {
                var error = Validator.Validate(tokenType, token!);

                // A device is created only when validation passes (returns null). An empty/whitespace
                // token must always fail with exactly 400 TOKEN_EMPTY, so no device is ever created.
                return error.HasValue
                       && error.Value.ErrorCode == RpcErrors.RpcErrors400.ErrorCode
                       && error.Value.Message == RpcErrors.RpcErrors400.TokenEmpty.Message;
            });
    }
}
