using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages.
///
/// <para>
/// "Typically, history imports are allowed for private chats with a mutual contact or supergroups with
/// change_info administrator rights". The check runs on every step of the flow, not only on the
/// confirmation, so an account that loses its rights halfway cannot keep importing.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class HistoryImportPeerValidatorTests
{
    private const long SelfUserId = 2010001;
    private const long OtherUserId = 2010002;
    private const long ChannelId = 1000001;

    [Fact]
    public async Task A_mutual_contact_can_receive_an_imported_history()
    {
        var validator = CreateValidator(mutual: true);

        var title = await validator.ValidateAsync(SelfUserId, new Peer(PeerType.User, OtherUserId));

        title.ShouldBe("John Doe");
    }

    [Fact]
    public async Task A_one_way_contact_is_USER_NOT_MUTUAL_CONTACT()
    {
        var validator = CreateValidator(mutual: false);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            validator.ValidateAsync(SelfUserId, new Peer(PeerType.User, OtherUserId)));

        exception.RpcError.Message.ShouldBe("USER_NOT_MUTUAL_CONTACT");
    }

    [Fact]
    public async Task A_bot_cannot_receive_an_imported_history()
    {
        var validator = CreateValidator(mutual: true, bot: true);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            validator.ValidateAsync(SelfUserId, new Peer(PeerType.User, OtherUserId)));

        exception.RpcError.Message.ShouldBe("USER_IS_BOT");
    }

    [Fact]
    public async Task Importing_into_the_chat_with_oneself_is_PEER_ID_INVALID()
    {
        var validator = CreateValidator(mutual: true);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            validator.ValidateAsync(SelfUserId, new Peer(PeerType.User, SelfUserId)));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [Fact]
    public async Task A_supergroup_admin_with_change_info_can_import()
    {
        var validator = CreateValidator(megagroup: true, creator: true);

        var title = await validator.ValidateAsync(SelfUserId, new Peer(PeerType.Channel, ChannelId));

        title.ShouldBe("Family");
    }

    [Fact]
    public async Task A_supergroup_member_without_rights_is_CHAT_ADMIN_REQUIRED()
    {
        var validator = CreateValidator(megagroup: true, creator: false);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            validator.ValidateAsync(SelfUserId, new Peer(PeerType.Channel, ChannelId)));

        exception.RpcError.Message.ShouldBe("CHAT_ADMIN_REQUIRED");
    }

    [Fact]
    public async Task A_broadcast_channel_is_not_a_chat_and_is_PEER_ID_INVALID()
    {
        var validator = CreateValidator(megagroup: false, creator: true);

        var exception = await Should.ThrowAsync<RpcException>(() =>
            validator.ValidateAsync(SelfUserId, new Peer(PeerType.Channel, ChannelId)));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [Fact]
    public async Task A_basic_group_passes_the_confirmation_check_but_not_the_import_itself()
    {
        var validator = CreateValidator();
        var chat = new Peer(PeerType.Chat, 500001);

        // The clients confirm on the basic group and convert it to a supergroup afterwards.
        await Should.NotThrowAsync(() => validator.ValidateAsync(SelfUserId, chat, allowLegacyChat: true));

        var exception = await Should.ThrowAsync<RpcException>(() => validator.ValidateAsync(SelfUserId, chat));
        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    [Fact]
    public void The_confirmation_text_names_the_destination()
    {
        var validator = CreateValidator();

        validator.BuildConfirmText(new Peer(PeerType.User, OtherUserId), "John Doe")
            .ShouldContain("John Doe");
        validator.BuildConfirmText(new Peer(PeerType.Channel, ChannelId), "Family")
            .ShouldContain("group");
    }

    private static HistoryImportPeerValidator CreateValidator(bool mutual = true, bool bot = false,
        bool megagroup = true, bool creator = true)
    {
        var user = new Mock<IUserReadModel>(MockBehavior.Loose);
        user.SetupGet(p => p.UserId).Returns(OtherUserId);
        user.SetupGet(p => p.FirstName).Returns("John");
        user.SetupGet(p => p.LastName).Returns("Doe");
        user.SetupGet(p => p.Bot).Returns(bot);
        user.SetupGet(p => p.IsDeleted).Returns(false);

        var userAppService = new Mock<IUserAppService>(MockBehavior.Loose);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(user.Object);
        userAppService.Setup(p => p.GetAsync(It.IsAny<long>())).ReturnsAsync(user.Object);

        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channel.SetupGet(p => p.Title).Returns("Family");
        channel.SetupGet(p => p.MegaGroup).Returns(megagroup);
        channel.SetupGet(p => p.Broadcast).Returns(!megagroup);
        channel.SetupGet(p => p.IsDeleted).Returns(false);
        channel.SetupGet(p => p.CreatorId).Returns(creator ? SelfUserId : 999);
        channel.SetupGet(p => p.AdminList).Returns([]);

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>())).ReturnsAsync(channel.Object);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long>())).ReturnsAsync(channel.Object);

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        var contact = mutual ? new Mock<IContactReadModel>(MockBehavior.Loose).Object : null;
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetContactQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(contact);

        var rightsChecker = new ChannelAdminRightsChecker(queryProcessor.Object, channelAppService.Object);

        return new HistoryImportPeerValidator(userAppService.Object, channelAppService.Object, rightsChecker,
            queryProcessor.Object);
    }
}
