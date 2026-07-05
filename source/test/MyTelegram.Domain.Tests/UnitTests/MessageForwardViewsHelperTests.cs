namespace MyTelegram.Domain.Tests.UnitTests;

public class MessageForwardViewsHelperTests
{
    private static MessageFwdHeader ChannelPostFwdHeader(long channelId = 100, int channelPost = 5)
    {
        return new MessageFwdHeader
        {
            FromId = new Peer(PeerType.Channel, channelId),
            ChannelPost = channelPost
        };
    }

    [Fact]
    public void ResolveForwardedViews_ChannelPost_ForwardedIntoGroup_ShouldNotCarryViews()
    {
        var views = MessageForwardViewsHelper.ResolveForwardedViews(
            isBroadcastDestination: false,
            fwdHeader: ChannelPostFwdHeader());

        views.ShouldBeNull();
    }

    [Fact]
    public void ResolveForwardedViews_ChannelPost_ForwardedIntoBroadcastChannel_ShouldStartViewCounter()
    {
        var views = MessageForwardViewsHelper.ResolveForwardedViews(
            isBroadcastDestination: true,
            fwdHeader: ChannelPostFwdHeader());

        views.ShouldBe(0);
    }

    [Fact]
    public void ResolveForwardedViews_NonChannelForward_IntoBroadcastChannel_ShouldNotCarryViews()
    {
        var fwdHeader = new MessageFwdHeader
        {
            FromId = new Peer(PeerType.User, 42)
        };

        MessageForwardViewsHelper.ResolveForwardedViews(true, fwdHeader).ShouldBeNull();
    }

    [Fact]
    public void ResolveForwardedViews_ChannelForwardWithoutPostId_ShouldNotCarryViews()
    {
        var fwdHeader = new MessageFwdHeader
        {
            FromId = new Peer(PeerType.Channel, 100),
            ChannelPost = null
        };

        MessageForwardViewsHelper.ResolveForwardedViews(true, fwdHeader).ShouldBeNull();
        MessageForwardViewsHelper.ResolveForwardedViews(false, fwdHeader).ShouldBeNull();
    }

    [Fact]
    public void ResolveForwardedViews_NullForwardHeader_ShouldNotCarryViews()
    {
        MessageForwardViewsHelper.ResolveForwardedViews(true, null).ShouldBeNull();
        MessageForwardViewsHelper.ResolveForwardedViews(false, null).ShouldBeNull();
    }

    [Theory]
    [InlineData(PeerType.Channel, 5, true)]
    [InlineData(PeerType.Channel, null, false)]
    [InlineData(PeerType.User, 5, false)]
    [InlineData(PeerType.Chat, 5, false)]
    public void IsForwardedChannelPost_ShouldOnlyMatchChannelPosts(PeerType fromPeerType, int? channelPost, bool expected)
    {
        var fwdHeader = new MessageFwdHeader
        {
            FromId = new Peer(fromPeerType, 1),
            ChannelPost = channelPost
        };

        MessageForwardViewsHelper.IsForwardedChannelPost(fwdHeader).ShouldBe(expected);
    }
}
