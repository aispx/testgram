using MyTelegram.Domain.Aggregates.Dialog;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Dialog;

/// <summary>
/// Feature: message <a href="https://corefork.telegram.org/api/drafts">drafts</a> on the dialog
/// aggregate. A peer holds one draft per topic — the chat itself, each forum topic and each monoforum
/// topic — and a clear only emits for the topics that actually have one, because every send with
/// <c>clear_draft</c> asks for a clear and would otherwise push an empty draft to all other sessions.
/// </summary>
public class DialogDraftTests : TestsFor<DialogAggregate>
{
    private const long OwnerUserId = 111;
    private static readonly Peer Channel = new(PeerType.Channel, 1001);
    private static readonly Peer MonoforumUser = new(PeerType.User, 222);

    public DialogDraftTests()
    {
        Fixture.Customize<DialogId>(x => x.FromFactory(() => DialogId.Create(OwnerUserId, Channel)));
    }

    [Fact]
    public void A_draft_in_a_chat_with_no_dialog_yet_keeps_the_owner_and_the_peer_it_was_given()
    {
        // Nothing created the dialog: typing into a chat you never messaged is exactly this case, and
        // reading the owner off the empty state stored the draft under owner 0 with no peer.
        SaveDraft(Draft("hello"));

        var @event = LastEvent<DraftSavedEvent>();
        @event.OwnerPeerId.ShouldBe(OwnerUserId);
        @event.Peer.ShouldBe(Channel);
        @event.Draft.Message.ShouldBe("hello");
    }

    [Fact]
    public void Clearing_a_draft_that_does_not_exist_emits_nothing()
    {
        ClearDrafts(DraftTopic.ChatLevel);

        Sut.UncommittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Clearing_the_chat_draft_names_the_chat_level_topic()
    {
        SaveDraft(Draft("hello"));

        ClearDrafts(DraftTopic.ChatLevel);

        var @event = LastEvent<DraftClearedEvent>();
        @event.OwnerPeerId.ShouldBe(OwnerUserId);
        @event.Peer.ShouldBe(Channel);
        @event.Topics.ShouldHaveSingleItem().IsChatLevel.ShouldBeTrue();
    }

    [Fact]
    public void A_draft_is_cleared_only_once()
    {
        SaveDraft(Draft("hello"));
        ClearDrafts(DraftTopic.ChatLevel);
        var eventCount = Sut.UncommittedEvents.Count();

        ClearDrafts(DraftTopic.ChatLevel);

        Sut.UncommittedEvents.Count().ShouldBe(eventCount);
    }

    [Fact]
    public void A_topic_draft_is_not_the_chat_draft()
    {
        SaveDraft(Draft("in the topic", topMsgId: 7));

        // The chat itself has no draft, so there is nothing to clear.
        ClearDrafts(DraftTopic.ChatLevel);

        Sut.UncommittedEvents.OfType<DraftClearedEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void Sending_into_a_topic_clears_that_topic_only()
    {
        SaveDraft(Draft("in the chat"));
        SaveDraft(Draft("in the topic", topMsgId: 7));

        ClearDrafts(new DraftTopic(7));

        LastEvent<DraftClearedEvent>().Topics.ShouldHaveSingleItem().TopMsgId.ShouldBe(7);
    }

    [Fact]
    public void Every_draft_of_a_dialog_is_cleared_in_one_event()
    {
        // clearAllDrafts asks for all of them at once: a second command for the same dialog in the same
        // request is deduplicated away by its msg_id, so they have to travel together.
        SaveDraft(Draft("in the chat"));
        SaveDraft(Draft("in the topic", topMsgId: 7));
        SaveDraft(Draft("in the monoforum topic", savedPeerId: MonoforumUser));

        ClearDrafts(DraftTopic.ChatLevel, new DraftTopic(7), new DraftTopic(null, MonoforumUser), new DraftTopic(9));

        // The topic that never had a draft is left out.
        var topics = LastEvent<DraftClearedEvent>().Topics;
        topics.Count.ShouldBe(3);
        topics.Select(p => p.Key).ShouldBe([DraftTopicKey.ChatLevel, "t7", $"m{MonoforumUser.PeerId}"], true);
    }

    private static Draft Draft(string message, int? topMsgId = null, Peer? savedPeerId = null)
    {
        return new Draft(false, false, null, message, 0, topMsgId: topMsgId, savedPeerId: savedPeerId);
    }

    private void SaveDraft(Draft draft)
    {
        Sut.SaveDraft(A<RequestInfo>(), OwnerUserId, Channel, draft);
        ApplyEvent((DraftSavedEvent)Sut.UncommittedEvents.Last().AggregateEvent);
    }

    private void ClearDrafts(params DraftTopic[] topics)
    {
        Sut.ClearDrafts(A<RequestInfo>(), OwnerUserId, Channel, [.. topics]);
        if (Sut.UncommittedEvents.LastOrDefault()?.AggregateEvent is DraftClearedEvent draftClearedEvent)
        {
            ApplyEvent(draftClearedEvent);
        }
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
