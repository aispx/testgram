using MyTelegram.Messenger.Services.Passport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get all saved <a href="https://corefork.telegram.org/passport">Telegram Passport</a> documents, <a href="https://corefork.telegram.org/passport/encryption#encryption">for more info see the passport docs »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getAllSecureValues"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAllSecureValuesHandler(IPassportValueStore passportValueStore)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetAllSecureValues, TVector<MyTelegram.Schema.ISecureValue>>
{
    protected override async Task<TVector<MyTelegram.Schema.ISecureValue>> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetAllSecureValues obj)
    {
        // No password check: the payload is end-to-end encrypted, and the client needs it before it can
        // even ask the user for the password that unlocks the passport secret.
        var documents = await passportValueStore.GetAllAsync(input.UserId);

        return await passportValueStore.ToSecureValuesAsync(input.UserId, documents);
    }
}
