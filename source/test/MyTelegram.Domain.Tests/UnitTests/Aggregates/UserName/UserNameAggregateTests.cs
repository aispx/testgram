using MyTelegram.Domain.Aggregates.UserName;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.UserName;

public class UserNameAggregateTests : TestsFor<UserNameAggregate>
{
    private const string ShortName = "gif";

    public UserNameAggregateTests()
    {
        Fixture.Customize<UserNameId>(c => c.FromFactory(() => UserNameId.Create(ShortName)));
    }

    [Fact]
    public void A_reserved_system_bot_may_take_a_username_shorter_than_the_ordinary_minimum()
    {
        var peer = new Peer(PeerType.User, MyTelegramConsts.GifSearchBotUserId);

        Sut.UpdateUserName(A<RequestInfo>(), peer, ShortName, null);

        var changed = Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<UserNameChangedEvent>();
        changed.UserName.ShouldBe(ShortName);
        changed.Peer.PeerId.ShouldBe(MyTelegramConsts.GifSearchBotUserId);
    }

    [Fact]
    public void An_ordinary_user_may_not()
    {
        var peer = new Peer(PeerType.User, MyTelegramConsts.UserIdInitId);

        var exception = Assert.Throws<RpcException>(() =>
            Sut.UpdateUserName(A<RequestInfo>(), peer, ShortName, null));

        exception.RpcError.Message.ShouldBe(RpcErrors.RpcErrors400.UsernameInvalid.Message);
        Sut.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void The_maximum_length_still_applies_to_a_reserved_system_bot()
    {
        var peer = new Peer(PeerType.User, MyTelegramConsts.GifSearchBotUserId);
        var tooLong = new string('a', MyTelegramConsts.UsernameMaxLength + 1);

        Assert.Throws<RpcException>(() => Sut.UpdateUserName(A<RequestInfo>(), peer, tooLong, null));
        Sut.UncommittedEvents.ShouldBeEmpty();
    }
}
