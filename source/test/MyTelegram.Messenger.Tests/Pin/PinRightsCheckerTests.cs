using EventFlow.Queries;
using Moq;
using MyTelegram.Abstractions;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.Pin;

/// <summary>
/// Feature: pinned messages — who is allowed to pin or unpin.
///
/// <para>
/// Pinning is governed by <c>pin_messages</c> in groups and by <c>edit_messages</c> in broadcast
/// channels, and a group may hand <c>pin_messages</c> to every member through its default banned
/// rights — unless a member carries a still-valid personal restriction.
/// See https://corefork.telegram.org/api/pin and https://corefork.telegram.org/api/rights
/// </para>
/// </summary>
public class PinRightsCheckerTests
{
    private const long ChannelId = 800000000001;
    private const long UserId = 2010001;
    private const long CreatorId = 2010002;

    [Fact]
    public async Task A_private_chat_needs_no_rights_at_all()
    {
        var checker = CreateChecker(BuildChannel());

        // Both sides of a one-to-one chat may pin; nothing must be looked up for it.
        await checker.CheckPinRightsAsync(RequestInput(), new Peer(PeerType.User, 2010009));
    }

    [Fact]
    public async Task A_member_may_pin_in_a_group_that_allows_pinning_to_everyone()
    {
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: false));

        await checker.CheckPinRightsAsync(RequestInput(), new Peer(PeerType.Channel, ChannelId));
    }

    [Fact]
    public async Task A_member_may_not_pin_in_a_group_that_reserves_pinning_for_admins()
    {
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: true));

        var error = await ShouldThrowRpcErrorAsync(checker, new Peer(PeerType.Channel, ChannelId));

        error.ShouldBe("CHAT_ADMIN_REQUIRED");
    }

    [Fact]
    public async Task An_admin_with_pin_messages_may_pin_even_when_the_group_forbids_it_by_default()
    {
        var channel = BuildChannel(pinMessagesBanned: true);
        var checker = CreateChecker(channel, hasPinAdminRight: true);

        await checker.CheckPinRightsAsync(RequestInput(), new Peer(PeerType.Channel, ChannelId));
    }

    [Fact]
    public async Task A_restricted_member_may_not_pin_even_when_the_group_allows_it()
    {
        // The chat defaults and the per-member restriction stack: a right is denied when either denies it.
        var member = BuildMember(pinMessagesBanned: true, untilDate: int.MaxValue);
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: false), channelMember: member);

        var error = await ShouldThrowRpcErrorAsync(checker, new Peer(PeerType.Channel, ChannelId));

        error.ShouldBe("PIN_RESTRICTED");
    }

    [Fact]
    public async Task An_expired_restriction_no_longer_blocks_pinning()
    {
        var member = BuildMember(pinMessagesBanned: true, untilDate: 1);
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: false), channelMember: member);

        await checker.CheckPinRightsAsync(RequestInput(), new Peer(PeerType.Channel, ChannelId));
    }

    [Fact]
    public async Task A_plain_member_may_not_pin_in_a_broadcast_channel_even_with_open_default_rights()
    {
        // Default banned rights only ever apply to groups: in a broadcast channel pinning always
        // requires the edit_messages admin right.
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: false, broadcast: true));

        var error = await ShouldThrowRpcErrorAsync(checker, new Peer(PeerType.Channel, ChannelId));

        error.ShouldBe("CHAT_ADMIN_REQUIRED");
    }

    [Fact]
    public async Task An_admin_with_edit_messages_may_pin_in_a_broadcast_channel()
    {
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: true, broadcast: true),
            hasEditAdminRight: true);

        await checker.CheckPinRightsAsync(RequestInput(), new Peer(PeerType.Channel, ChannelId));
    }

    [Fact]
    public async Task A_non_member_may_not_pin()
    {
        // Pinning writes to the chat, so a non-member is rejected even in a channel anyone can preview.
        var checker = CreateChecker(BuildChannel(pinMessagesBanned: false), isMember: false);

        var error = await ShouldThrowRpcErrorAsync(checker, new Peer(PeerType.Channel, ChannelId));

        error.ShouldBe("CHANNEL_PRIVATE");
    }

    private static async Task<string> ShouldThrowRpcErrorAsync(IPinRightsChecker checker, Peer peer)
    {
        var exception = await Should.ThrowAsync<RpcException>(
            () => checker.CheckPinRightsAsync(RequestInput(), peer));

        return exception.RpcError.Message;
    }

    private static PinRightsChecker CreateChecker(
        IChannelReadModel channel,
        bool isMember = true,
        bool hasPinAdminRight = false,
        bool hasEditAdminRight = false,
        IChannelMemberReadModel? channelMember = null)
    {
        var channelAppService = new Mock<IChannelAppService>();
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long>())).ReturnsAsync(channel);
        channelAppService.Setup(p => p.IsChannelMemberAsync(It.IsAny<long>(), It.IsAny<long>()))
            .ReturnsAsync(isMember);

        var adminRights = new ChatAdminRights { PinMessages = hasPinAdminRight, EditMessages = hasEditAdminRight };
        var adminRightsChecker = new Mock<IChannelAdminRightsChecker>();
        adminRightsChecker
            .Setup(p => p.HasChatAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>()))
            .ReturnsAsync((long _, long _, Func<ChatAdminRights, bool> check) => check(adminRights));
        adminRightsChecker
            .Setup(p => p.CheckAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>(), It.IsAny<RpcError?>()))
            .Returns((long _, long _, Func<ChatAdminRights, bool> check, RpcError? rpcError) =>
            {
                if (!check(adminRights))
                {
                    (rpcError ?? RpcErrors.RpcErrors400.ChatAdminRequired).ThrowRpcError();
                }

                return Task.CompletedTask;
            });

        var queryProcessor = new Mock<IQueryProcessor>();
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetChannelMemberByUserIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channelMember);

        return new PinRightsChecker(channelAppService.Object, adminRightsChecker.Object, queryProcessor.Object);
    }

    /// <summary>chatBannedRights.pin_messages is flag 17.</summary>
    private const int PinMessagesBannedFlag = 1 << 17;

    private static IChannelReadModel BuildChannel(bool pinMessagesBanned = true, bool broadcast = false)
    {
        var channel = new Mock<IChannelReadModel>();
        channel.SetupGet(p => p.ChannelId).Returns(ChannelId);
        channel.SetupGet(p => p.CreatorId).Returns(CreatorId);
        channel.SetupGet(p => p.Broadcast).Returns(broadcast);
        channel.SetupGet(p => p.DefaultBannedRights)
            .Returns(ChatBannedRights.FromValue(pinMessagesBanned ? PinMessagesBannedFlag : 0, int.MaxValue));

        return channel.Object;
    }

    private static IChannelMemberReadModel BuildMember(bool pinMessagesBanned, int untilDate)
    {
        var member = new Mock<IChannelMemberReadModel>();
        member.SetupGet(p => p.UserId).Returns(UserId);
        member.SetupGet(p => p.ChannelId).Returns(ChannelId);
        member.SetupGet(p => p.BannedRights).Returns(pinMessagesBanned ? PinMessagesBannedFlag : 0);
        member.SetupGet(p => p.UntilDate).Returns(untilDate);

        return member.Object;
    }

    private static IRequestInput RequestInput()
    {
        var input = new Mock<IRequestInput>();
        input.SetupGet(p => p.UserId).Returns(UserId);

        return input.Object;
    }
}
