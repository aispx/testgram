using System.Linq;
using MyTelegram.Domain.Aggregates.Messaging;
using Shouldly;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Messaging;

/// <summary>
/// Regression tests for the reaction merge in <see cref="MessageAggregate.UpdateMessageReactions"/>.
/// The command now carries only the acting user's own reactions and the aggregate merges them against
/// its authoritative state, so a reaction by one user can never clobber a concurrent reaction by another.
/// </summary>
public class MessageReactionMergeTests : TestsFor<MessageAggregate>
{
    private const long UserA = 111;
    private const long UserB = 222;

    public MessageReactionMergeTests()
    {
        // MessageId has a strict 'message-[guid]' identity syntax that AutoFixture cannot satisfy.
        Fixture.Customize<MessageId>(c => c.FromFactory(() => MessageId.Create(1000, 1)));
    }

    [Fact]
    public void Reaction_by_one_user_does_not_clobber_another_users_reaction()
    {
        Sut.CreateOutboxMessage(A<RequestInfo>(), A<MessageItem>());

        Sut.UpdateMessageReactions(A<RequestInfo>(), UserA, [new Reaction(UserA, "\U0001F44D", null, 1)]);
        Sut.UpdateMessageReactions(A<RequestInfo>(), UserB, [new Reaction(UserB, "❤", null, 2)]);

        var reactions = LastReactions();
        reactions.Count(r => r.UserId == UserA).ShouldBe(1);
        reactions.Count(r => r.UserId == UserB).ShouldBe(1);
    }

    [Fact]
    public void Reacting_again_replaces_only_the_callers_own_reaction()
    {
        Sut.CreateOutboxMessage(A<RequestInfo>(), A<MessageItem>());

        Sut.UpdateMessageReactions(A<RequestInfo>(), UserA, [new Reaction(UserA, "\U0001F44D", null, 1)]);
        Sut.UpdateMessageReactions(A<RequestInfo>(), UserB, [new Reaction(UserB, "❤", null, 2)]);

        // User A changes their reaction: B is preserved, A is replaced, not duplicated.
        Sut.UpdateMessageReactions(A<RequestInfo>(), UserA, [new Reaction(UserA, "\U0001F525", null, 3)]);

        var reactions = LastReactions();
        reactions.Count(r => r.UserId == UserA).ShouldBe(1);
        reactions.Single(r => r.UserId == UserA).Emoticon.ShouldBe("\U0001F525");
        reactions.Count(r => r.UserId == UserB).ShouldBe(1);
    }

    [Fact]
    public void Removing_own_reaction_leaves_other_users_untouched()
    {
        Sut.CreateOutboxMessage(A<RequestInfo>(), A<MessageItem>());

        Sut.UpdateMessageReactions(A<RequestInfo>(), UserA, [new Reaction(UserA, "\U0001F44D", null, 1)]);
        Sut.UpdateMessageReactions(A<RequestInfo>(), UserB, [new Reaction(UserB, "❤", null, 2)]);

        // User A clears their reactions (empty list): B must remain.
        Sut.UpdateMessageReactions(A<RequestInfo>(), UserA, []);

        var reactions = LastReactions();
        reactions.Count(r => r.UserId == UserA).ShouldBe(0);
        reactions.Count(r => r.UserId == UserB).ShouldBe(1);
    }

    private List<Reaction> LastReactions() =>
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<MessageReactionsUpdatedEvent>().Reactions;
}
