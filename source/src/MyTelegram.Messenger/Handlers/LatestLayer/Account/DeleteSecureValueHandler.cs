using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Delete stored <a href="https://corefork.telegram.org/passport">Telegram Passport</a> documents, <a href="https://corefork.telegram.org/passport/encryption#encryption">for more info see the passport docs »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.deleteSecureValue"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteSecureValueHandler(IPassportValueStore passportValueStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestDeleteSecureValue, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestDeleteSecureValue obj)
    {
        var types = PassportRequestHelper.ToConstructorIds(obj.Types);

        // Deletes the referenced files as well - a scan that no value points at is unreachable, and
        // leaving it behind would keep the user's document on the server after they removed it.
        await passportValueStore.DeleteAsync(input.UserId, types);

        return new TBoolTrue();
    }
}
