using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.AccountDeletion;

/// <summary>
/// Feature: account deletion — when <c>account.deleteAccount</c> deletes right away and when it
/// grants the account a week of grace.
///
/// <para>
/// Without a 2FA password, or with the password supplied, the account goes immediately. With a 2FA
/// password the caller did not supply, deletion is delayed for a week — but only when the password
/// is older than a week and the account was actually used recently; otherwise it goes immediately
/// too. The delayed case answers <c>420 2FA_CONFIRM_WAIT_%d</c> and notifies the owner.
/// See https://corefork.telegram.org/api/account-deletion
/// </para>
/// </summary>
public class DeleteAccountHandlerTests
{
    private const long UserId = 2010001;
    private const long RequestAuthKeyId = 777;
    private const string PhoneNumber = "12222222222";

    [RequiresMongoDbFact]
    public async Task An_account_without_a_2fa_password_is_deleted_immediately()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService, password: null);

        var result = await InvokeAsync(handler, new RequestDeleteAccount { Reason = "bye" });

        result.ShouldBeOfType<TBoolTrue>();
        deletionService.Verify(p => p.DeleteAccountAsync(UserId, "bye", It.IsAny<RequestInfo>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task A_wrong_2fa_password_is_PASSWORD_HASH_INVALID_and_deletes_nothing()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService, password: OldPassword(), srpValid: false);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, DeleteWithPassword()));

        exception.RpcError.Message.ShouldBe("PASSWORD_HASH_INVALID");
        deletionService.Verify(p => p.DeleteAccountAsync(It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<RequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task The_correct_2fa_password_deletes_immediately_even_for_an_active_account()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SetLastOnlineAsync(mongo.Database, DateTime.UtcNow.AddHours(-1));
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService, password: OldPassword(), srpValid: true);

        var result = await InvokeAsync(handler, DeleteWithPassword());

        result.ShouldBeOfType<TBoolTrue>();
        deletionService.Verify(p => p.DeleteAccountAsync(UserId, It.IsAny<string>(), It.IsAny<RequestInfo>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task An_active_account_with_an_old_password_is_deleted_in_a_week_and_the_owner_is_told()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SetLastOnlineAsync(mongo.Database, DateTime.UtcNow.AddHours(-1));
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var messageSender = new Mock<IObjectMessageSender>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService, password: OldPassword(),
            messageSender: messageSender);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, new RequestDeleteAccount { Reason = string.Empty }));

        exception.RpcError.ErrorCode.ShouldBe(420);
        exception.RpcError.Message.ShouldStartWith("2FA_CONFIRM_WAIT_");
        deletionService.Verify(p => p.SchedulePendingAsync(UserId, PhoneNumber, string.Empty,
            It.IsAny<DateTime>(), It.IsAny<RequestInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        deletionService.Verify(p => p.DeleteAccountAsync(It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<RequestInfo>(), It.IsAny<CancellationToken>()), Times.Never);

        // The owner's other sessions get the confirmphone link; the session that asked does not.
        messageSender.Verify(p => p.PushMessageToPeerAsync(It.IsAny<Peer>(), It.IsAny<TUpdates>(),
            RequestAuthKeyId, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(),
            It.IsAny<int?>(), It.IsAny<long>(), It.IsAny<PushData?>(), It.IsAny<List<long>?>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task A_password_set_within_the_last_week_does_not_buy_the_account_a_delay()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SetLastOnlineAsync(mongo.Database, DateTime.UtcNow.AddHours(-1));
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService,
            password: Password(DateTime.UtcNow.AddDays(-1)));

        var result = await InvokeAsync(handler, new RequestDeleteAccount { Reason = string.Empty });

        result.ShouldBeOfType<TBoolTrue>();
        deletionService.Verify(p => p.DeleteAccountAsync(UserId, string.Empty, It.IsAny<RequestInfo>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task An_account_nobody_used_this_week_is_deleted_immediately_despite_its_2fa_password()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SetLastOnlineAsync(mongo.Database, DateTime.UtcNow.AddDays(-30));
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, deletionService, password: OldPassword());

        var result = await InvokeAsync(handler, new RequestDeleteAccount { Reason = string.Empty });

        result.ShouldBeOfType<TBoolTrue>();
        deletionService.Verify(p => p.DeleteAccountAsync(UserId, string.Empty, It.IsAny<RequestInfo>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task A_second_call_reports_the_remaining_wait_instead_of_pushing_the_deadline_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SetLastOnlineAsync(mongo.Database, DateTime.UtcNow.AddHours(-1));
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByUserIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountDeletionDocument
            {
                UserId = UserId,
                PhoneNumber = PhoneNumber,
                DeleteAt = DateTime.UtcNow.AddDays(3)
            });
        var handler = CreateHandler(mongo.Database, deletionService, password: OldPassword());

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, new RequestDeleteAccount { Reason = string.Empty }));

        exception.RpcError.Message.ShouldStartWith("2FA_CONFIRM_WAIT_");
        deletionService.Verify(p => p.SchedulePendingAsync(It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<RequestInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static RequestDeleteAccount DeleteWithPassword() => new()
    {
        Reason = string.Empty,
        Password = new TInputCheckPasswordSRP { SrpId = 1, A = new byte[8], M1 = new byte[8] }
    };

    private static UserPasswordDocument OldPassword() => Password(DateTime.UtcNow.AddDays(-30));

    private static UserPasswordDocument Password(DateTime updatedAt) => new()
    {
        Id = UserId,
        Salt1 = [1],
        Salt2 = [2],
        PasswordHash = [3],
        PasswordUpdatedAt = updatedAt
    };

    private static async Task SetLastOnlineAsync(IMongoDatabase database, DateTime lastOnline)
    {
        await database.GetCollection<UserStatusMongoModel>("user_status").InsertOneAsync(new UserStatusMongoModel
        {
            UserId = UserId,
            LastOnline = lastOnline,
            Online = false
        });
    }

    private static object CreateHandler(IMongoDatabase database,
        Mock<IAccountDeletionService> deletionService,
        UserPasswordDocument? password,
        bool srpValid = false,
        Mock<IObjectMessageSender>? messageSender = null)
    {
        var twoFactorService = new Mock<ITwoFactorService>(MockBehavior.Loose);
        twoFactorService.Setup(p => p.GetPasswordAsync(UserId)).ReturnsAsync(password);
        twoFactorService.Setup(p => p.GetUserIdBySrpIdAsync(It.IsAny<long>())).ReturnsAsync(UserId);
        twoFactorService.Setup(p => p.VerifySrpAsync(UserId, It.IsAny<long>(), It.IsAny<byte[]>(),
            It.IsAny<byte[]>())).ReturnsAsync(srpValid);

        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(UserId);
        user.SetupGet(p => p.PhoneNumber).Returns(PhoneNumber);
        user.SetupGet(p => p.IsDeleted).Returns((bool?)null);

        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(user.Object);

        deletionService.Setup(p => p.SchedulePendingAsync(It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<RequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long userId, string phone, string reason, DateTime deleteAt, RequestInfo _,
                CancellationToken _) => new AccountDeletionDocument
            {
                UserId = userId,
                PhoneNumber = phone,
                Reason = reason,
                DeleteAt = deleteAt,
                Hash = "hash"
            });

        return new Handlers.LatestLayer.Account.DeleteAccountHandler(
            twoFactorService.Object,
            deletionService.Object,
            userAppService.Object,
            (messageSender ?? new Mock<IObjectMessageSender>(MockBehavior.Loose)).Object,
            database,
            StatsTestOptions.Create(),
            NullLogger<Handlers.LatestLayer.Account.DeleteAccountHandler>.Instance);
    }

    private static Task<object?> InvokeAsync(object handler, RequestDeleteAccount request) =>
        HandlerInvoker.InvokeAsync(handler, request, UserId, RequestAuthKeyId);

}
