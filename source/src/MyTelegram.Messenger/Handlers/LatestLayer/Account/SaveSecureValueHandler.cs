using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Services.TwoFactor;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Securely save <a href="https://corefork.telegram.org/passport">Telegram Passport</a> document, <a href="https://corefork.telegram.org/passport/encryption#encryption">for more info see the passport docs »</a>
/// Possible errors
/// Code Type Description
/// 400 PASSWORD_REQUIRED A <a href="https://corefork.telegram.org/api/srp">2FA password</a> must be configured to use Telegram Passport.
/// 400 SECURE_SECRET_REQUIRED A secure secret is required.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveSecureValue"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveSecureValueHandler(
    ITwoFactorService twoFactorService,
    IPassportValueStore passportValueStore,
    IPassportVerificationStore passportVerificationStore,
    IAccessHashHelper2 accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveSecureValue, MyTelegram.Schema.ISecureValue>
{
    protected override async Task<MyTelegram.Schema.ISecureValue> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestSaveSecureValue obj)
    {
        if (obj.Value is not TInputSecureValue value)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            return null!;
        }

        // Telegram Passport is keyed off the 2FA password: without one there is nothing to encrypt the
        // passport secret with. https://corefork.telegram.org/passport/encryption
        if (await twoFactorService.GetPasswordAsync(input.UserId) == null)
        {
            RpcErrors.RpcErrors400.PasswordRequired.ThrowRpcError();
        }

        var secureSettings = await twoFactorService.GetSecureSettingsAsync(input.UserId);

        // The client must quote the fingerprint of the passport secret it used, so a value encrypted
        // under a stale secret (password changed on another device) is rejected rather than stored
        // undecryptable.
        if (secureSettings == null || secureSettings.SecureSecretId != obj.SecureSecretId)
        {
            RpcErrors.RpcErrors400.SecureSecretRequired.ThrowRpcError();
        }

        PassportRequestHelper.EnsureFieldsAllowed(value);

        await EnsurePlainDataVerifiedAsync(input.UserId, value.PlainData);
        await CheckFileAccessHashesAsync(input, value);

        var document = await passportValueStore.SaveAsync(input.UserId, value);
        var values = await passportValueStore.ToSecureValuesAsync(input.UserId, [document]);

        if (values.Count == 0)
        {
            RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
        }

        return values[0];
    }

    /// <summary>
    /// A plain phone/email may only be saved once Telegram itself verified it - that verification is the
    /// only guarantee the receiving service gets, since the value travels in clear text.
    /// </summary>
    private async Task EnsurePlainDataVerifiedAsync(long userId, ISecurePlainData? plainData)
    {
        switch (plainData)
        {
            case TSecurePlainPhone phone:
                if (!await passportVerificationStore.IsPhoneVerifiedAsync(userId, phone.Phone))
                {
                    RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
                }

                break;

            case TSecurePlainEmail email:
                if (!await passportVerificationStore.IsEmailVerifiedAsync(userId, email.Email))
                {
                    RpcErrors.RpcErrors400.EmailInvalid.ThrowRpcError();
                }

                break;
        }
    }

    /// <summary>
    /// <c>inputSecureFile</c> reuses a file the user uploaded earlier, and its access hash is
    /// session-derived like every other one. The store additionally checks ownership, so a guessed id
    /// cannot pull in somebody else's scan.
    /// </summary>
    private async Task CheckFileAccessHashesAsync(IRequestInput input, TInputSecureValue value)
    {
        foreach (var file in EnumerateFiles(value).OfType<TInputSecureFile>())
        {
            await accessHashHelper.CheckAccessHashAsync(input, file.Id, file.AccessHash, AccessHashType.Document);
        }
    }

    private static IEnumerable<IInputSecureFile> EnumerateFiles(TInputSecureValue value)
    {
        if (value.FrontSide != null) yield return value.FrontSide;
        if (value.ReverseSide != null) yield return value.ReverseSide;
        if (value.Selfie != null) yield return value.Selfie;

        foreach (var file in value.Files ?? []) yield return file;
        foreach (var file in value.Translation ?? []) yield return file;
    }
}
