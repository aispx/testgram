using EventFlow;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Core;
using MyTelegram.Domain.Aggregates.AppCode;
using MyTelegram.Messenger.Services.AccountDeletion;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.TwoFactor;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Messenger.Tests.AccountDeletion;

/// <summary>
/// Feature: account deletion — cancelling a delayed deletion by confirming the phone number.
///
/// <para>
/// The owner opens the <c>confirmphone</c> link, the client calls <c>account.sendConfirmPhoneCode</c>
/// with the hash from it, and <c>account.confirmPhone</c> with the code received. That cancels the
/// deletion and logs out the session that asked for it.
/// See https://corefork.telegram.org/api/account-deletion
/// </para>
/// </summary>
public class ConfirmPhoneFlowTests
{
    private const long UserId = 2010001;
    private const long AttackerPermAuthKeyId = 999;
    private const string PhoneNumber = "12222222222";
    private const string LinkHash = "linkhash";
    private const string PhoneCodeHash = "codehash";
    private const string Code = "12345";

    [Fact]
    public async Task A_confirmphone_hash_of_another_account_is_HASH_INVALID()
    {
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByHashAsync(LinkHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending(userId: 2010002));
        var handler = CreateSendHandler(deletionService, new Mock<ICommandBus>(MockBehavior.Loose));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            HandlerInvoker.InvokeAsync(handler, new RequestSendConfirmPhoneCode { Hash = LinkHash }, UserId));

        exception.RpcError.Message.ShouldBe("HASH_INVALID");
    }

    [Fact]
    public async Task Sending_the_confirmation_code_creates_a_code_for_the_number_being_deleted()
    {
        var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByHashAsync(LinkHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending());
        var handler = CreateSendHandler(deletionService, commandBus);

        var result = await HandlerInvoker.InvokeAsync(handler,
            new RequestSendConfirmPhoneCode { Hash = LinkHash }, UserId);

        result.ShouldBeOfType<TSentCode>().PhoneCodeHash.ShouldNotBeNullOrEmpty();
        commandBus.Verify(p => p.PublishAsync(
            It.Is<CreateAppCodeCommand>(c => c.PhoneNumber == PhoneNumber && c.Code == Code),
            It.IsAny<CancellationToken>()), Times.Once);
        deletionService.Verify(p => p.SetPhoneCodeHashAsync(UserId, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_right_code_cancels_the_deletion_and_logs_out_the_session_that_asked_for_it()
    {
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByUserIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending(PhoneCodeHash));
        var handler = CreateConfirmHandler(deletionService, AppCode(Code));

        var result = await HandlerInvoker.InvokeAsync(handler,
            new RequestConfirmPhone { PhoneCodeHash = PhoneCodeHash, PhoneCode = Code }, UserId);

        result.ShouldBeOfType<TBoolTrue>();
        deletionService.Verify(p => p.CancelPendingAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
        deletionService.Verify(p => p.RevokeSessionAsync(UserId, AttackerPermAuthKeyId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_wrong_code_is_counted_and_leaves_the_deletion_in_place()
    {
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByUserIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending(PhoneCodeHash));
        var handler = CreateConfirmHandler(deletionService, AppCode(Code));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            HandlerInvoker.InvokeAsync(handler,
                new RequestConfirmPhone { PhoneCodeHash = PhoneCodeHash, PhoneCode = "00000" }, UserId));

        exception.RpcError.Message.ShouldBe("PHONE_CODE_INVALID");
        deletionService.Verify(p => p.IncrementFailedConfirmCountAsync(UserId, It.IsAny<CancellationToken>()),
            Times.Once);
        deletionService.Verify(p => p.CancelPendingAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_code_hash_that_was_never_sent_for_this_deletion_is_CODE_HASH_INVALID()
    {
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByUserIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending(PhoneCodeHash));
        var handler = CreateConfirmHandler(deletionService, AppCode(Code));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            HandlerInvoker.InvokeAsync(handler,
                new RequestConfirmPhone { PhoneCodeHash = "somethingelse", PhoneCode = Code }, UserId));

        exception.RpcError.Message.ShouldBe("CODE_HASH_INVALID");
    }

    [Fact]
    public async Task An_expired_code_is_PHONE_CODE_EXPIRED()
    {
        var deletionService = new Mock<IAccountDeletionService>(MockBehavior.Loose);
        deletionService.Setup(p => p.GetPendingByUserIdAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Pending(PhoneCodeHash));
        var handler = CreateConfirmHandler(deletionService,
            AppCode(Code, expire: (int)DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            HandlerInvoker.InvokeAsync(handler,
                new RequestConfirmPhone { PhoneCodeHash = PhoneCodeHash, PhoneCode = Code }, UserId));

        exception.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static AccountDeletionDocument Pending(string? phoneCodeHash = null, long userId = UserId) => new()
    {
        UserId = userId,
        PhoneNumber = PhoneNumber,
        Hash = LinkHash,
        DeleteAt = DateTime.UtcNow.AddDays(7),
        PhoneCodeHash = phoneCodeHash,
        RequestedByPermAuthKeyId = AttackerPermAuthKeyId
    };

    private static IAppCodeReadModel AppCode(string code, int? expire = null)
    {
        var appCode = new Mock<IAppCodeReadModel>(MockBehavior.Loose);
        appCode.SetupGet(p => p.Code).Returns(code);
        appCode.SetupGet(p => p.PhoneNumber).Returns(PhoneNumber);
        appCode.SetupGet(p => p.PhoneCodeHash).Returns(PhoneCodeHash);
        appCode.SetupGet(p => p.Expire)
            .Returns(expire ?? (int)DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds());

        return appCode.Object;
    }

    private static Handlers.LatestLayer.Account.SendConfirmPhoneCodeHandler CreateSendHandler(
        Mock<IAccountDeletionService> deletionService,
        Mock<ICommandBus> commandBus)
    {
        var codeGenerator = new Mock<IVerificationCodeGenerator>(MockBehavior.Loose);
        codeGenerator.Setup(p => p.Generate()).Returns(Code);

        return new Handlers.LatestLayer.Account.SendConfirmPhoneCodeHandler(
            deletionService.Object,
            commandBus.Object,
            codeGenerator.Object,
            StatsTestOptions.Create());
    }

    private static Handlers.LatestLayer.Account.ConfirmPhoneHandler CreateConfirmHandler(
        Mock<IAccountDeletionService> deletionService,
        IAppCodeReadModel appCode)
    {
        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor.Setup(p => p.ProcessAsync(It.IsAny<GetLatestAppCodeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(appCode);

        return new Handlers.LatestLayer.Account.ConfirmPhoneHandler(
            deletionService.Object,
            new Mock<ITwoFactorService>(MockBehavior.Loose).Object,
            queryProcessor.Object,
            new Mock<ICommandBus>(MockBehavior.Loose).Object,
            NullLogger<Handlers.LatestLayer.Account.ConfirmPhoneHandler>.Instance);
    }
}
