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
    IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestSetSecureValueErrors, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestSetSecureValueErrors obj)
    {
        // This method can only be called by bots
        // Validate access hash

        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;

        // In a full implementation, we would:
        // 1. Store the errors in a collection for the user to see
        // 2. Send an update to the user about the errors
        // 3. Mark the passport data as invalid

        // For now, just return success
        // The errors are in obj.Errors (TVector<ISecureValueError>)

        return new TBoolTrue();
    }
}