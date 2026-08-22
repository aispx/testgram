using EventFlow;
using EventFlow.Queries;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.Domain.Aggregates.Device;
using MyTelegram.Domain.Aggregates.UserName;
using MyTelegram.Core;
using MyTelegram.EventBus;
using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.AccountDeletion;

/// <summary>
/// Feature: account deletion — what deleting an account actually does, and the bookkeeping of a
/// deletion that was delayed by a week.
///
/// <para>
/// Deleting wipes the profile, frees every username, revokes every session and drops the 2FA
/// password; messages the user sent to other chats stay where they are, exactly like on the official
/// server. A deletion requested without the 2FA password is parked for a week under a hash that the
/// <c>confirmphone</c> link carries, and only one sweeper pass may execute it.
/// See https://corefork.telegram.org/api/account-deletion
/// </para>
/// </summary>
public class AccountDeletionServiceTests
{
    private const long UserId = 2010001;
    private const long OtherPermAuthKeyId = 555;

    [RequiresMongoDbFact]
    public async Task Deleting_an_account_wipes_the_profile_frees_usernames_and_revokes_every_session()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
        var eventBus = new Mock<IEventBus>(MockBehavior.Loose);
        var twoFactorService = new Mock<ITwoFactorService>(MockBehavior.Loose);
        var service = CreateService(mongo.Database, commandBus, eventBus, twoFactorService);

        await service.DeleteAccountAsync(UserId, "no longer needed");

        commandBus.Verify(p => p.PublishAsync(
            It.Is<DeleteAccountCommand>(c => c.Reason == "no longer needed"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Both the legacy username and the fragment one go back into circulation.
        commandBus.Verify(p => p.PublishAsync(
            It.Is<DeleteUserNameCommand>(c => c.AggregateId == UserNameId.Create("glebxdlol")),
            It.IsAny<CancellationToken>()), Times.Once);
        commandBus.Verify(p => p.PublishAsync(
            It.Is<DeleteUserNameCommand>(c => c.AggregateId == UserNameId.Create("blockchain")),
            It.IsAny<CancellationToken>()), Times.Once);

        commandBus.Verify(p => p.PublishAsync(
            It.Is<UnRegisterDeviceForAuthKeyCommand>(c => c.PermAuthKeyId == OtherPermAuthKeyId),
            It.IsAny<CancellationToken>()), Times.Once);

        // No session survives, so nothing is spared the revocation.
        eventBus.Verify(p => p.PublishAsync(
            It.Is<SessionRevokedEvent>(e => e.PermAuthKeyId == 0 &&
                                            e.UserId == UserId &&
                                            e.RevokedPermAuthKeyIdList.Contains(OtherPermAuthKeyId)),
            It.IsAny<string?>()), Times.Once);

        twoFactorService.Verify(p => p.RemovePasswordAsync(UserId), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task An_already_deleted_account_is_not_deleted_twice()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
        var service = CreateService(mongo.Database, commandBus, isDeleted: true);

        await service.DeleteAccountAsync(UserId, string.Empty);

        commandBus.Verify(p => p.PublishAsync(It.IsAny<DeleteAccountCommand>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [RequiresMongoDbFact]
    public async Task A_pending_deletion_is_found_by_the_hash_of_its_confirmphone_link()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);

        var document = await service.SchedulePendingAsync(UserId, "12222222222", "reason",
            DateTime.UtcNow.AddDays(7), RequestInfo.Empty with { PermAuthKeyId = OtherPermAuthKeyId });

        document.Hash.ShouldNotBeNullOrEmpty();
        (await service.GetPendingByHashAsync(document.Hash))!.UserId.ShouldBe(UserId);
        (await service.GetPendingByHashAsync("deadbeef")).ShouldBeNull();
        (await service.GetPendingByUserIdAsync(UserId))!.RequestedByPermAuthKeyId.ShouldBe(OtherPermAuthKeyId);

        await service.CancelPendingAsync(UserId);
        (await service.GetPendingByUserIdAsync(UserId)).ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public async Task Only_a_due_deletion_is_claimed_and_a_claimed_one_is_not_handed_out_twice()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);
        var now = DateTime.UtcNow;

        await service.SchedulePendingAsync(UserId, "12222222222", string.Empty, now.AddDays(7), RequestInfo.Empty);

        (await service.ClaimNextDuePendingAsync(now, TimeSpan.FromMinutes(5))).ShouldBeNull();

        var due = now.AddDays(8);
        (await service.ClaimNextDuePendingAsync(due, TimeSpan.FromMinutes(5)))!.UserId.ShouldBe(UserId);

        // Still leased: a second pass a minute later must not pick it up again.
        (await service.ClaimNextDuePendingAsync(due.AddMinutes(1), TimeSpan.FromMinutes(5))).ShouldBeNull();

        // The lease expires, so a crashed pass does not park the deletion forever.
        (await service.ClaimNextDuePendingAsync(due.AddMinutes(6), TimeSpan.FromMinutes(5)))!.UserId.ShouldBe(UserId);
    }

    [RequiresMongoDbFact]
    public async Task Wrong_confirmation_codes_are_counted_and_a_new_code_resets_the_counter()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var service = CreateService(mongo.Database);

        await service.SchedulePendingAsync(UserId, "12222222222", string.Empty, DateTime.UtcNow.AddDays(7),
            RequestInfo.Empty);

        (await service.IncrementFailedConfirmCountAsync(UserId)).ShouldBe(1);
        (await service.IncrementFailedConfirmCountAsync(UserId)).ShouldBe(2);

        await service.SetPhoneCodeHashAsync(UserId, "hash");
        var pending = await service.GetPendingByUserIdAsync(UserId);
        pending!.PhoneCodeHash.ShouldBe("hash");
        pending.FailedConfirmCount.ShouldBe(0);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static AccountDeletionService CreateService(IMongoDatabase database,
        Mock<ICommandBus>? commandBus = null,
        Mock<IEventBus>? eventBus = null,
        Mock<ITwoFactorService>? twoFactorService = null,
        bool isDeleted = false)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(UserId);
        user.SetupGet(p => p.UserName).Returns("glebxdlol");
        user.SetupGet(p => p.PhoneNumber).Returns("12222222222");
        user.SetupGet(p => p.IsDeleted).Returns(isDeleted ? true : null);
        user.SetupGet(p => p.Usernames).Returns([
            new UsernameInfo { Username = "glebxdlol", Editable = true, Active = true },
            new UsernameInfo { Username = "blockchain", Editable = false, Active = true }
        ]);

        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(user.Object);

        var device = new Mock<IDeviceReadModel>(MockBehavior.Loose);
        device.SetupGet(p => p.PermAuthKeyId).Returns(OtherPermAuthKeyId);
        device.SetupGet(p => p.TempAuthKeyId).Returns(556);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetDeviceByUserIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([device.Object]);

        return new AccountDeletionService(database,
            (commandBus ?? new Mock<ICommandBus>(MockBehavior.Loose)).Object,
            queryProcessor.Object,
            (eventBus ?? new Mock<IEventBus>(MockBehavior.Loose)).Object,
            (twoFactorService ?? new Mock<ITwoFactorService>(MockBehavior.Loose)).Object,
            userAppService.Object,
            new Mock<ILogger<AccountDeletionService>>(MockBehavior.Loose).Object);
    }
}
