namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Messaging;

/// <summary>
/// Feature: message threads — the reply counter shown on the message that starts a thread.
///
/// <para>
/// <c>messageReplies.max_id</c> is the id of the latest message in the comment section and
/// <c>replies_pts</c> the pts that goes with it, so both have to follow every reply; a client that sees
/// a stale <c>max_id</c> believes there is nothing new to fetch. <c>recent_repliers</c> is a small
/// preview list and must stay within its bound.
/// See https://corefork.telegram.org/api/threads
/// </para>
/// </summary>
public class MessageThreadRepliesTests : TestsFor<MessageAggregate>
{
    public MessageThreadRepliesTests()
    {
        Fixture.Customize<MessageId>(c => c.FromFactory(() => MessageId.Create(1, 1)));
    }

    [Fact]
    public void Every_reply_advances_the_counter_and_the_latest_message_id()
    {
        CreateChannelMessage();
        var replier = A<long>().ToUserPeer();

        Sut.ReplyToMessage(A<RequestInfo>(), replier, repliesPts: 11, messageId: 101);
        Sut.ReplyToMessage(A<RequestInfo>(), replier, repliesPts: 12, messageId: 102);

        var reply = LastReply();
        reply.Replies.ShouldBe(2);
        reply.MaxId.ShouldBe(102);
        reply.RepliesPts.ShouldBe(12);
    }

    [Fact]
    public void Recent_repliers_keeps_the_newest_ones_within_the_limit()
    {
        CreateChannelMessage();
        var repliers = Enumerable.Range(0, MyTelegramConsts.MaxRecentRepliersCount + 2)
            .Select(_ => A<long>().ToUserPeer())
            .ToList();

        var messageId = 100;
        foreach (var replier in repliers)
        {
            Sut.ReplyToMessage(A<RequestInfo>(), replier, repliesPts: ++messageId, messageId: messageId);
        }

        var recentRepliers = LastReply().RecentRepliers!;
        recentRepliers.Count.ShouldBe(MyTelegramConsts.MaxRecentRepliersCount);

        // Newest first, and the ones that dropped off are the oldest.
        recentRepliers[0].PeerId.ShouldBe(repliers[^1].PeerId);
        recentRepliers.ShouldNotContain(p => p.PeerId == repliers[0].PeerId);
    }

    [Fact]
    public void The_same_replier_replying_twice_is_listed_once()
    {
        CreateChannelMessage();
        var replier = A<long>().ToUserPeer();
        var other = A<long>().ToUserPeer();

        Sut.ReplyToMessage(A<RequestInfo>(), replier, repliesPts: 11, messageId: 101);
        Sut.ReplyToMessage(A<RequestInfo>(), other, repliesPts: 12, messageId: 102);
        Sut.ReplyToMessage(A<RequestInfo>(), replier, repliesPts: 13, messageId: 103);

        var reply = LastReply();
        reply.Replies.ShouldBe(3);
        reply.RecentRepliers!.Count(p => p.PeerId == replier.PeerId).ShouldBe(1);
        reply.RecentRepliers[0].PeerId.ShouldBe(replier.PeerId);
    }

    [Fact]
    public void Deleting_a_reply_takes_it_off_the_counter()
    {
        CreateChannelMessage();
        Sut.ReplyToMessage(A<RequestInfo>(), A<long>().ToUserPeer(), repliesPts: 11, messageId: 101);
        Sut.ReplyToMessage(A<RequestInfo>(), A<long>().ToUserPeer(), repliesPts: 12, messageId: 102);

        Sut.DecrementMessageReply(pts: 13);

        var decremented = Sut.UncommittedEvents.Last().AggregateEvent
            .ShouldBeOfType<MessageReplyCountDecrementedEvent>();
        decremented.Replies.ShouldBe(1);
        decremented.Pts.ShouldBe(13);
    }

    [Fact]
    public void A_message_without_replies_is_not_decremented_below_zero()
    {
        CreateChannelMessage();

        Sut.DecrementMessageReply(pts: 13);

        Sut.UncommittedEvents.ShouldNotContain(p => p.AggregateEvent is MessageReplyCountDecrementedEvent);
    }

    private MessageReply LastReply()
    {
        return Sut.UncommittedEvents
            .Select(p => p.AggregateEvent)
            .OfType<ReplyChannelMessageCompletedEvent>()
            .Last()
            .Reply;
    }

    private void CreateChannelMessage()
    {
        var ownerPeer = A<long>().ToChannelPeer();
        var senderPeer = A<long>().ToUserPeer();
        var messageItem = new MessageItem(
            ownerPeer,
            ownerPeer,
            senderPeer,
            senderPeer.PeerId,
            A<int>(),
            "test message",
            DateTime.UtcNow.ToTimestamp(),
            1,
            true);

        var outboxMessageCreatedEvent = new OutboxMessageCreatedEvent(A<RequestInfo>(),
            messageItem,
            null, null,
            true,
            1,
            null, null);

        Sut.ApplyEvents([
            ADomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent>(outboxMessageCreatedEvent, 1)
        ]);
    }
}
