using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

/// <summary>
/// Feature: the @ badge counter of a dialog, see https://corefork.telegram.org/api/mentions.
/// It goes up once per mention, down once per message read through readMessageContents, and straight
/// to zero on readMentions.
/// </summary>
public class DialogMentionCounterTests : TestsFor<DialogAggregate>
{
    private const long OwnerUserId = 111;
    private static readonly Peer Channel = new(PeerType.Channel, 1001);

    public DialogMentionCounterTests()
    {
        Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(OwnerUserId, Channel)));
    }

    [Fact]
    public void A_mention_in_a_dialog_that_does_not_exist_yet_is_ignored()
    {
        // Emitting here would store an event with a null peer: the mentioned user simply has no
        // dialog with the channel yet.
        Sut.CreateMention(10);

        Sut.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Every_mention_bumps_the_counter()
    {
        CreateDialog();

        Sut.CreateMention(10);
        Sut.CreateMention(11);

        LastEvent<MentionCreatedEvent>().UnreadMentionsCount.ShouldBe(2);
    }

    [Fact]
    public void Reading_one_mention_takes_the_counter_down_by_one()
    {
        CreateDialog();
        Mention(10);
        Mention(11);

        Sut.ReadMention(10);

        LastEvent<MentionReadEvent>().UnreadMentionsCount.ShouldBe(1);
    }

    [Fact]
    public void The_counter_never_goes_negative()
    {
        CreateDialog();

        Sut.ReadMention(10);

        LastEvent<MentionReadEvent>().UnreadMentionsCount.ShouldBe(0);
    }

    [Fact]
    public void Reading_all_mentions_clears_the_counter_in_one_event()
    {
        CreateDialog();
        Mention(10);
        Mention(11);

        Sut.ReadUnreadMentions();

        var @event = LastEvent<UnreadMentionsReadEvent>();
        @event.UnreadMentionsCount.ShouldBe(0);
        @event.OwnerUserId.ShouldBe(OwnerUserId);
        @event.ToPeer.ShouldBe(Channel);
    }

    [Fact]
    public void Reading_all_mentions_of_a_dialog_that_does_not_exist_throws()
    {
        Should.Throw<Exception>(() => Sut.ReadUnreadMentions());
    }

    [Fact]
    public void A_recount_puts_the_counter_back_in_step()
    {
        CreateDialog();
        Mention(10);
        Mention(11);

        // Both mentions were deleted along with their messages.
        Sut.SyncUnreadMentionsCount(0);

        LastEvent<UnreadMentionsCountSyncedEvent>().UnreadMentionsCount.ShouldBe(0);
    }

    [Fact]
    public void A_recount_that_changes_nothing_emits_nothing()
    {
        CreateDialog();
        Mention(10);
        var eventCount = Sut.UncommittedEvents.Count();

        Sut.SyncUnreadMentionsCount(1);

        Sut.UncommittedEvents.Count().ShouldBe(eventCount);
    }

    [Fact]
    public void A_negative_recount_is_ignored()
    {
        CreateDialog();
        Mention(10);
        var eventCount = Sut.UncommittedEvents.Count();

        Sut.SyncUnreadMentionsCount(-1);

        Sut.UncommittedEvents.Count().ShouldBe(eventCount);
    }

    private void CreateDialog()
    {
        Sut.CreateDialog(A<RequestInfo>(), OwnerUserId, Channel, 0, 0);
        ApplyEvent((DialogCreatedEvent)Sut.UncommittedEvents.Last().AggregateEvent);
    }

    private void Mention(int messageId)
    {
        Sut.CreateMention(messageId);
        ApplyEvent((MentionCreatedEvent)Sut.UncommittedEvents.Last().AggregateEvent);
    }

    private void ApplyEvent<TEvent>(TEvent aggregateEvent)
        where TEvent : IAggregateEvent<DialogAggregate, DialogId>
    {
        // EventFlow rejects an event whose sequence number doesn't follow the current
        // version, so each applied event has to advance it.
        Sut.ApplyEvents([ADomainEvent<DialogAggregate, DialogId, TEvent>(aggregateEvent, Sut.Version + 1)]);
    }

    private T LastEvent<T>() where T : class
    {
        return Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<T>();
    }
}
