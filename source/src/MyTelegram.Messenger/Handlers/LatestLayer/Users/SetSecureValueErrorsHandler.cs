using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Notify the user that the sent <a href="https://corefork.telegram.org/passport">passport</a> data contains some errors The user will not be able to re-submit their Passport data to you until the errors are fixed (the contents of the field for which you returned the error must change).Use this if the data submitted by the user doesn't satisfy the standards your service requires for any reason. For example, if a birthday date seems invalid, a submitted document is blurry, a scan shows evidence of tampering, etc. Supply some details in the error message to make sure the user knows how to correct the issues.
/// Possible errors
/// Code Type Description
/// 400 DATA_HASH_SIZE_INVALID The size of the specified secureValueErrorData.data_hash is invalid.
/// 400 HASH_SIZE_INVALID The size of the specified secureValueError.hash is invalid.
/// 400 USER_BOT_REQUIRED This method can only be called by a bot.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.setSecureValueErrors"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✖] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetSecureValueErrorsHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IPassportErrorStore passportErrorStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestSetSecureValueErrors, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Users.RequestSetSecureValueErrors obj)
    {
        // Only a bot may reject documents - a user has nothing to reject.
        var caller = await queryProcessor.ProcessAsync(new GetUserByIdQuery(input.UserId));
        if (caller is not { Bot: true })
        {
            RpcErrors.RpcErrors400.UserBotRequired.ThrowRpcError();
        }

        if (obj.Id is not TInputUser inputUser)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputUser.UserId, inputUser.AccessHash,
            AccessHashType.User);

        var target = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (target == null || target.Bot || target.IsDeleted == true)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var errors = ValidateErrors(obj.Errors);

        // Stored per (user, bot): account.getAuthorizationForm hands them back so the user can see what
        // this particular service rejected.
        await passportErrorStore.SetAsync(inputUser.UserId, input.UserId, errors);

        return new TBoolTrue();
    }

    /// <summary>
    /// Every hash a bot quotes is a SHA-256 of something the server issued, so a wrong length can only
    /// come from a bot that made one up. Telegram reports the <c>data_hash</c> of
    /// <c>secureValueErrorData</c> under its own error code.
    /// </summary>
    private static List<ISecureValueError> ValidateErrors(TVector<ISecureValueError>? errors)
    {
        var result = new List<ISecureValueError>();
        if (errors == null)
        {
            return result;
        }

        foreach (var error in errors)
        {
            switch (error)
            {
                case TSecureValueErrorData e:
                    EnsureHashSize(e.DataHash.Length, RpcErrors.RpcErrors400.DataHashSizeInvalid);
                    break;
                case TSecureValueError e:
                    EnsureHashSize(e.Hash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorFrontSide e:
                    EnsureHashSize(e.FileHash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorReverseSide e:
                    EnsureHashSize(e.FileHash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorSelfie e:
                    EnsureHashSize(e.FileHash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorFile e:
                    EnsureHashSize(e.FileHash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorTranslationFile e:
                    EnsureHashSize(e.FileHash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
                    break;
                case TSecureValueErrorFiles e:
                    EnsureHashSizes(e.FileHash);
                    break;
                case TSecureValueErrorTranslationFiles e:
                    EnsureHashSizes(e.FileHash);
                    break;
                default:
                    continue;
            }

            result.Add(error);
        }

        return result;
    }

    private static void EnsureHashSizes(TVector<ReadOnlyMemory<byte>>? hashes)
    {
        if (hashes == null || hashes.Count == 0)
        {
            RpcErrors.RpcErrors400.HashSizeInvalid.ThrowRpcError();
            return;
        }

        foreach (var hash in hashes)
        {
            EnsureHashSize(hash.Length, RpcErrors.RpcErrors400.HashSizeInvalid);
        }
    }

    private static void EnsureHashSize(int length, RpcError error)
    {
        if (length != PassportRequestHelper.HashLength)
        {
            error.ThrowRpcError();
        }
    }
}
