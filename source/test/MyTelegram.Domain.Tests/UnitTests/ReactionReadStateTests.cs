namespace MyTelegram.Domain.Tests.UnitTests;

public class ReactionReadStateTests
{
    [Fact]
    public void ShouldCreateDistinctKeysForPeerScopes()
    {
        var peer = new Peer(PeerType.Channel, 100);
        var savedPeer = new Peer(PeerType.User, 200);

        ReactionReadState.GetKey(peer).ShouldBe("read_reactions:5:100");
        ReactionReadState.GetKey(peer, 300).ShouldBe("read_reactions:5:100:topic:300");
        ReactionReadState.GetKey(peer, savedPeerId: savedPeer).ShouldBe("read_reactions:5:100:saved:3:200");
    }

    [Fact]
    public void ShouldOnlyTreatForeignReactionsAfterReadDateAsUnread()
    {
        var unread = CreateReaction(22, 200);
        var own = CreateReaction(11, 300);
        var read = CreateReaction(22, 100);

        ReactionReadState.IsUnread(unread, 11, 100).ShouldBeTrue();
        ReactionReadState.IsUnread(own, 11, 100).ShouldBeFalse();
        ReactionReadState.IsUnread(read, 11, 100).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("bad", 0)]
    [InlineData("123", 123)]
    public void ShouldParseReadDate(string? value, int expected)
    {
        ReactionReadState.ParseReadDate(value).ShouldBe(expected);
    }

    private static MessagePeerReaction CreateReaction(long userId, int date)
    {
        return new MessagePeerReaction
        {
            PeerId = new Peer(PeerType.User, userId),
            SenderUserId = userId,
            Date = date,
            Reaction = null!
        };
    }
}
