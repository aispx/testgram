using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Feature: stats-api, Task 7.3 — example/edge-case unit tests for the individual
/// <see cref="StatsAccessController"/> error branches.
///
/// <para>These complement the ordered-access-check property task (Property 1, Task 7.2) by pinning down
/// each single failing branch of the fixed access-check order (Requirement 1.5) and the exact RPC error it
/// surfaces:</para>
/// <list type="bullet">
///   <item>A channel that does not resolve to an existing read model → <c>CHANNEL_INVALID</c>
///   (Requirement 1.1).</item>
///   <item>A private (no public username) broadcast channel whose caller is not a participant →
///   <c>CHANNEL_PRIVATE</c> (Requirement 1.2).</item>
///   <item>An <c>InputPeer</c> that cannot be resolved → <c>PEER_ID_INVALID</c> (Requirement 1.3a,
///   <see cref="StatsAccessController.ResolvePeerForStoryStatsAsync"/>).</item>
///   <item>A supergroup where a broadcast channel is required → <c>BROADCAST_REQUIRED</c>
///   (Requirement 2.4).</item>
///   <item>A broadcast channel where a supergroup is required → <c>MEGAGROUP_REQUIRED</c>
///   (Requirement 3.4).</item>
/// </list>
///
/// <para>The four collaborators (<see cref="IChannelAppService"/>, <see cref="IChannelAdminRightsChecker"/>,
/// <see cref="IPeerHelper"/>, <see cref="IQueryProcessor"/>) are mocked with Moq — the mocking approach the
/// rest of the codebase uses — so each branch is exercised in isolation without any real infrastructure.</para>
/// </summary>
public class StatsAccessControllerErrorBranchTests
{
    private const long CallerUserId = 42;

    private readonly Mock<IChannelAppService> _channelAppService = new(MockBehavior.Loose);
    private readonly Mock<IChannelAdminRightsChecker> _adminRightsChecker = new(MockBehavior.Loose);
    private readonly Mock<IPeerHelper> _peerHelper = new(MockBehavior.Loose);
    private readonly Mock<IQueryProcessor> _queryProcessor = new(MockBehavior.Loose);

    public StatsAccessControllerErrorBranchTests()
    {
        // A non-anonymous, non-bot caller so the caller-type gate (Requirement 1.4) always passes and the
        // test reaches the branch under test.
        _peerHelper.Setup(x => x.IsBotUser(CallerUserId)).Returns(false);
    }

    private StatsAccessController CreateController() => new(
        _channelAppService.Object,
        _adminRightsChecker.Object,
        _peerHelper.Object,
        _queryProcessor.Object);

    private static IRequestInput CreateInput()
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(x => x.UserId).Returns(CallerUserId);
        return input.Object;
    }

    private static IChannelReadModel CreateChannel(long channelId, bool broadcast, bool megaGroup, string? userName)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(x => x.ChannelId).Returns(channelId);
        channel.SetupGet(x => x.Broadcast).Returns(broadcast);
        channel.SetupGet(x => x.MegaGroup).Returns(megaGroup);
        channel.SetupGet(x => x.UserName).Returns(userName);
        return channel.Object;
    }

    private static IInputChannel InputChannel(long channelId) => new TInputChannel { ChannelId = channelId, AccessHash = 0 };

    // ----- CHANNEL_INVALID: channel does not resolve to an existing read model (Requirement 1.1) -----

    [Fact]
    public async Task Unresolved_channel_throws_CHANNEL_INVALID()
    {
        _channelAppService.Setup(x => x.GetAsync(It.IsAny<long?>())).ReturnsAsync((IChannelReadModel?)null);
        var controller = CreateController();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            controller.ResolveChannelForStatsAsync(CreateInput(), InputChannel(100), StatsChannelKind.BroadcastOnly, checkJoinable: true));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("CHANNEL_INVALID");
    }

    // ----- CHANNEL_PRIVATE: private broadcast channel, caller is not a participant (Requirement 1.2) -----

    [Fact]
    public async Task Private_broadcast_channel_with_non_member_caller_throws_CHANNEL_PRIVATE()
    {
        // Broadcast channel with no public username => private. Kind check passes (broadcast), joinability fails.
        var channel = CreateChannel(channelId: 200, broadcast: true, megaGroup: false, userName: null);
        _channelAppService.Setup(x => x.GetAsync(It.IsAny<long?>())).ReturnsAsync(channel);
        _channelAppService.Setup(x => x.IsChannelMemberAsync(CallerUserId, 200)).ReturnsAsync(false);
        var controller = CreateController();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            controller.ResolveChannelForStatsAsync(CreateInput(), InputChannel(200), StatsChannelKind.BroadcastOnly, checkJoinable: true));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("CHANNEL_PRIVATE");
    }

    // ----- PEER_ID_INVALID: InputPeer cannot be resolved (Requirement 1.3a) -----

    [Fact]
    public async Task Unresolved_peer_throws_PEER_ID_INVALID()
    {
        // An unresolvable peer surfaces as NotSupportedException from the peer helper, which the controller
        // maps to PEER_ID_INVALID.
        _peerHelper.Setup(x => x.GetPeer(It.IsAny<IInputPeer?>(), It.IsAny<long>())).Throws<NotSupportedException>();
        var controller = CreateController();
        var peer = new Mock<IInputPeer>(MockBehavior.Loose).Object;

        var ex = await Should.ThrowAsync<RpcException>(() =>
            controller.ResolvePeerForStoryStatsAsync(CreateInput(), peer));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    // ----- BROADCAST_REQUIRED: supergroup where a broadcast channel is required (Requirement 2.4) -----

    [Fact]
    public async Task Supergroup_when_broadcast_required_throws_BROADCAST_REQUIRED()
    {
        var supergroup = CreateChannel(channelId: 300, broadcast: false, megaGroup: true, userName: "public_group");
        _channelAppService.Setup(x => x.GetAsync(It.IsAny<long?>())).ReturnsAsync(supergroup);
        var controller = CreateController();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            controller.ResolveChannelForStatsAsync(CreateInput(), InputChannel(300), StatsChannelKind.BroadcastOnly, checkJoinable: true));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("BROADCAST_REQUIRED");
    }

    // ----- MEGAGROUP_REQUIRED: broadcast channel where a supergroup is required (Requirement 3.4) -----

    [Fact]
    public async Task Broadcast_channel_when_megagroup_required_throws_MEGAGROUP_REQUIRED()
    {
        var broadcast = CreateChannel(channelId: 400, broadcast: true, megaGroup: false, userName: "public_channel");
        _channelAppService.Setup(x => x.GetAsync(It.IsAny<long?>())).ReturnsAsync(broadcast);
        var controller = CreateController();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            controller.ResolveChannelForStatsAsync(CreateInput(), InputChannel(400), StatsChannelKind.MegagroupOnly, checkJoinable: false));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("MEGAGROUP_REQUIRED");
    }
}
