using Moq;
using MyTelegram.Messenger.Handlers.LatestLayer.Account;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.Services.Passport;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Messenger.Tests.AccountDeletion;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: the passport secret rides on <c>account.updatePasswordSettings</c>.
///
/// <para>
/// "The account.passwordInputSettings constructor in new_settings should contain only the
/// new_secure_setting parameter" — so a Passport setup call carries no new_algo and no
/// new_password_hash, and must not be mistaken for a request to disable 2FA.
/// See https://corefork.telegram.org/passport/encryption#passport-secret-encryption
/// </para>
/// </summary>
public class PasswordSecureSettingsTests
{
    private const long UserId = 2010001;

    [Fact]
    public async Task Setting_up_passport_stores_the_secret_and_leaves_the_password_alone()
    {
        var twoFactor = CreateTwoFactorService();

        await HandlerInvoker.InvokeAsync(CreateHandler(twoFactor), Request(SecureSettings()), UserId);

        twoFactor.Verify(p => p.SetSecureSettingsAsync(UserId,
                It.Is<byte[]>(s => s.Length == 40),
                It.Is<byte[]>(s => s.Length == 48),
                1234567890L),
            Times.Once);
        twoFactor.Verify(p => p.RemovePasswordAsync(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Setting_up_passport_does_not_delete_the_stored_documents()
    {
        var valueStore = new Mock<IPassportValueStore>(MockBehavior.Loose);

        await HandlerInvoker.InvokeAsync(
            CreateHandler(CreateTwoFactorService(), valueStore), Request(SecureSettings()), UserId);

        valueStore.Verify(p => p.DeleteAllAsync(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task A_secret_under_a_legacy_algorithm_is_NEW_SALT_INVALID()
    {
        var settings = SecureSettings();
        settings.SecureAlgo = new TSecurePasswordKdfAlgoSHA512 { Salt = new byte[8] };

        var exception = await Should.ThrowAsync<RpcException>(() => HandlerInvoker.InvokeAsync(
            CreateHandler(CreateTwoFactorService()), Request(settings), UserId));

        exception.RpcError.Message.ShouldBe("NEW_SALT_INVALID");
    }

    [Fact]
    public async Task An_empty_secret_drops_passport_without_touching_the_password()
    {
        var settings = SecureSettings();
        settings.SecureSecret = Array.Empty<byte>();

        var twoFactor = CreateTwoFactorService();
        var valueStore = new Mock<IPassportValueStore>(MockBehavior.Loose);

        await HandlerInvoker.InvokeAsync(CreateHandler(twoFactor, valueStore), Request(settings), UserId);

        twoFactor.Verify(p => p.ClearSecureSettingsAsync(UserId), Times.Once);
        twoFactor.Verify(p => p.RemovePasswordAsync(It.IsAny<long>()), Times.Never);
        valueStore.Verify(p => p.DeleteAllAsync(UserId), Times.Once);
    }

    [Fact]
    public async Task Disabling_the_password_destroys_the_passport_data()
    {
        // "If the password is disabled, all Telegram Passport data is lost."
        var twoFactor = CreateTwoFactorService();
        twoFactor.Setup(p => p.GetPasswordAsync(It.IsAny<long>()))
            .ReturnsAsync(new UserPasswordDocument { Id = UserId });
        twoFactor.Setup(p => p.VerifySrpAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<byte[]>(),
            It.IsAny<byte[]>())).ReturnsAsync(true);

        var valueStore = new Mock<IPassportValueStore>(MockBehavior.Loose);

        var request = new RequestUpdatePasswordSettings
        {
            Password = new TInputCheckPasswordSRP { SrpId = 1, A = new byte[256], M1 = new byte[32] },
            NewSettings = new TPasswordInputSettings
            {
                Flags = 1,
                NewAlgo = new TPasswordKdfAlgoUnknown(),
                NewPasswordHash = []
            }
        };

        await HandlerInvoker.InvokeAsync(CreateHandler(twoFactor, valueStore), request, UserId);

        twoFactor.Verify(p => p.RemovePasswordAsync(UserId), Times.Once);
        valueStore.Verify(p => p.DeleteAllAsync(UserId), Times.Once);
    }

    private static TSecureSecretSettings SecureSettings()
    {
        return new TSecureSecretSettings
        {
            SecureAlgo = new TSecurePasswordKdfAlgoPBKDF2HMACSHA512iter100000 { Salt = new byte[40] },
            SecureSecret = new byte[48],
            SecureSecretId = 1234567890L
        };
    }

    private static RequestUpdatePasswordSettings Request(ISecureSecretSettings secureSettings)
    {
        return new RequestUpdatePasswordSettings
        {
            Password = new TInputCheckPasswordEmpty(),
            NewSettings = new TPasswordInputSettings
            {
                // Only flag 2 (new_secure_settings) is set, exactly as the passport docs prescribe.
                Flags = 4,
                NewSecureSettings = secureSettings
            }
        };
    }

    private static Mock<ITwoFactorService> CreateTwoFactorService()
    {
        var twoFactor = new Mock<ITwoFactorService>(MockBehavior.Loose);
        // No stored password: TInputCheckPasswordEmpty is only accepted in that case.
        twoFactor.Setup(p => p.GetPasswordAsync(It.IsAny<long>())).ReturnsAsync((UserPasswordDocument?)null);

        return twoFactor;
    }

    private static UpdatePasswordSettingsHandler CreateHandler(Mock<ITwoFactorService> twoFactor,
        Mock<IPassportValueStore>? valueStore = null)
    {
        return new UpdatePasswordSettingsHandler(twoFactor.Object,
            new Mock<IEmailSender>(MockBehavior.Loose).Object,
            (valueStore ?? new Mock<IPassportValueStore>(MockBehavior.Loose)).Object,
            new Mock<IPassportErrorStore>(MockBehavior.Loose).Object,
            new Mock<IPassportVerificationStore>(MockBehavior.Loose).Object);
    }
}
