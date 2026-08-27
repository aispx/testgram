using EventFlow.Aggregates;
using Moq;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.ReadModel.ReadModelLocators;

namespace MyTelegram.Messenger.Tests.Drafts;

/// <summary>
/// Feature: where a <a href="https://corefork.telegram.org/api/drafts">draft</a> is stored. One row per
/// (dialog, topic), so a draft written in a forum or monoforum topic does not overwrite the draft of the
/// chat. The chat level draft keeps the bare dialog id, which is why the rows written before topics were
/// supported need no migration.
/// </summary>
public class DraftReadModelLocatorTests
{
    private const long OwnerUserId = 2_000_001;
    private static readonly Peer ChannelPeer = new(PeerType.Channel, 1_555_001);
    private static readonly Peer MonoforumUser = new(PeerType.User, 4242);
    private static readonly string DialogId =
        MyTelegram.Domain.Aggregates.Dialog.DialogId.Create(OwnerUserId, ChannelPeer).Value;

    private readonly DraftReadModelLocator _locator = new();

    [Fact]
    public void The_chat_draft_is_stored_under_the_bare_dialog_id()
    {
        var ids = _locator.GetReadModelIds(SavedEvent(Draft("hello")));

        ids.ShouldBe([DialogId]);
    }

    [Fact]
    public void A_forum_topic_draft_gets_its_own_row()
    {
        var ids = _locator.GetReadModelIds(SavedEvent(Draft("hello", topMsgId: 7)));

        ids.ShouldBe([$"{DialogId}_t7"]);
    }

    [Fact]
    public void A_monoforum_topic_draft_gets_its_own_row()
    {
        var ids = _locator.GetReadModelIds(SavedEvent(Draft("hello", savedPeerId: MonoforumUser)));

        ids.ShouldBe([$"{DialogId}_m{MonoforumUser.PeerId}"]);
    }

    [Fact]
    public void A_clear_of_several_topics_names_every_row_it_deletes()
    {
        var ids = _locator.GetReadModelIds(ClearedEvent([
            DraftTopic.ChatLevel,
            new DraftTopic(7),
            new DraftTopic(null, MonoforumUser)
        ]));

        ids.ShouldBe([DialogId, $"{DialogId}_t7", $"{DialogId}_m{MonoforumUser.PeerId}"]);
    }

    [Fact]
    public void A_clear_written_before_topics_existed_still_names_the_chat_draft()
    {
        // Events already in the store carry no topic list at all, and that meant the draft of the chat.
        var ids = _locator.GetReadModelIds(ClearedEvent(null));

        ids.ShouldBe([DialogId]);
    }

    private static Draft Draft(string message, int? topMsgId = null, Peer? savedPeerId = null)
    {
        return new Draft(false, false, null, message, 0, topMsgId: topMsgId, savedPeerId: savedPeerId);
    }

    private static IDomainEvent SavedEvent(Draft draft)
    {
        return DomainEvent(new DraftSavedEvent(RequestInfo.Empty, OwnerUserId, ChannelPeer, draft));
    }

    private static IDomainEvent ClearedEvent(List<DraftTopic>? topics)
    {
        return DomainEvent(new DraftClearedEvent(RequestInfo.Empty, OwnerUserId, ChannelPeer, topics!));
    }

    private static IDomainEvent DomainEvent(IAggregateEvent aggregateEvent)
    {
        var domainEvent = new Mock<IDomainEvent>(MockBehavior.Loose);
        domainEvent.Setup(p => p.GetAggregateEvent()).Returns(aggregateEvent);
        domainEvent.Setup(p => p.GetIdentity())
            .Returns(MyTelegram.Domain.Aggregates.Dialog.DialogId.Create(OwnerUserId, ChannelPeer));

        return domainEvent.Object;
    }
}
