using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Discussion;

/// <summary>
/// Feature: message threads — which message is the root of a thread.
///
/// <para>
/// "Replies to messages in a thread are part of the same thread, and do not spawn new threads", so
/// answering a comment must be counted on the thread starter and not on the answered comment. The
/// same root is used when a reply is deleted and when a reply is mirrored to the @replies peer, which
/// is why the rule lives in one helper. See https://corefork.telegram.org/api/threads
/// </para>
/// </summary>
public class MessageThreadHelperTests
{
    [Fact]
    public void A_message_that_is_not_a_reply_belongs_to_no_thread()
    {
        MessageThreadHelper.GetThreadRootMessageId(null, null).ShouldBeNull();
        MessageThreadHelper.GetThreadRootMessageId(null, new TInputReplyToStory { StoryId = 7 }).ShouldBeNull();
    }

    [Fact]
    public void A_direct_reply_identifies_the_thread_by_the_answered_message()
    {
        // The first reply to message 420 opens thread 420: no top_msg_id is sent yet.
        MessageThreadHelper
            .GetThreadRootMessageId(null, new TInputReplyToMessage { ReplyToMsgId = 420 })
            .ShouldBe(420);
    }

    [Fact]
    public void A_reply_inside_a_thread_keeps_the_root_of_that_thread()
    {
        // Answering comment 555 of thread 420 must not spawn a thread on 555.
        MessageThreadHelper
            .GetThreadRootMessageId(null, new TInputReplyToMessage { ReplyToMsgId = 555, TopMsgId = 420 })
            .ShouldBe(420);
    }

    [Fact]
    public void The_root_resolved_on_the_message_wins_over_the_request()
    {
        // MessageAppService resolves the root server-side for clients that only send reply_to_msg_id,
        // and forum topics carry the topic id there as well.
        MessageThreadHelper
            .GetThreadRootMessageId(420, new TInputReplyToMessage { ReplyToMsgId = 555 })
            .ShouldBe(420);
    }

    [Fact]
    public void Zero_ids_are_treated_as_absent()
    {
        MessageThreadHelper
            .GetThreadRootMessageId(0, new TInputReplyToMessage { ReplyToMsgId = 0, TopMsgId = 0 })
            .ShouldBeNull();
    }
}
