namespace MyTelegram;

/// <summary>
/// Resolves the root of the <a href="https://corefork.telegram.org/api/threads">thread</a> a message
/// belongs to.
/// <para>
/// "Replies to messages in a thread are part of the same thread, and do not spawn new threads": a
/// reply carries the root in <c>top_msg_id</c> (forum topics and answers to a comment), and only a
/// direct reply to a message that starts a thread identifies the root by <c>reply_to_msg_id</c>
/// alone. Everything that counts, deletes or lists thread messages must agree on this rule.
/// </para>
/// </summary>
public static class MessageThreadHelper
{
    public static int? GetThreadRootMessageId(int? topMsgId, IInputReplyTo? inputReplyTo)
    {
        if (topMsgId is > 0)
        {
            return topMsgId;
        }

        if (inputReplyTo is not TInputReplyToMessage inputReplyToMessage)
        {
            return null;
        }

        if (inputReplyToMessage.TopMsgId is > 0)
        {
            return inputReplyToMessage.TopMsgId;
        }

        return inputReplyToMessage.ReplyToMsgId > 0 ? inputReplyToMessage.ReplyToMsgId : null;
    }

    public static int? GetThreadRootMessageId(MessageItem messageItem)
    {
        return GetThreadRootMessageId(messageItem.TopMsgId, messageItem.InputReplyTo);
    }
}
