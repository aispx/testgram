namespace MyTelegram;

/// <summary>
/// A draft is not one per chat: a forum topic and a
/// <a href="https://corefork.telegram.org/api/monoforum">monoforum</a> topic each keep their own, and
/// clients read which one an <c>updateDraftMessage</c> is about from <c>top_msg_id</c> /
/// <c>saved_peer_id</c> (Android <c>MessagesController.processUpdateArray</c>). The topic is only ever
/// named on the way in through <c>inputReplyToMessage.top_msg_id</c> /
/// <c>monoforum_peer_id</c> — <c>messages.saveDraft</c> has no parameter of its own for it.
///
/// <para>This is the key that separates them in storage. The chat level draft keeps the bare
/// <c>DialogId</c> as its id, so the rows written before topics were supported need no migration.</para>
/// </summary>
public static class DraftTopicKey
{
    /// <summary>The key of a chat level draft: the one <c>dialog.draft</c> is built from.</summary>
    public const string ChatLevel = "";

    public static string Create(int? topMsgId, Peer? savedPeerId)
    {
        if (savedPeerId != null)
        {
            return $"m{savedPeerId.PeerId}";
        }

        return topMsgId is > 0 ? $"t{topMsgId.Value}" : ChatLevel;
    }

    public static string Create(Draft draft)
    {
        return Create(draft.TopMsgId, draft.SavedPeerId);
    }

    /// <summary>
    /// The topics named by a <c>DraftClearedEvent</c>. Events written before topics were supported carry
    /// no list at all, which meant the draft of the chat.
    /// </summary>
    public static IReadOnlyList<DraftTopic> OrChatLevel(List<DraftTopic>? topics)
    {
        return topics is { Count: > 0 } ? topics : [DraftTopic.ChatLevel];
    }

    public static bool IsChatLevel(string topicKey)
    {
        return topicKey.Length == 0;
    }

    /// <summary>The read model id of a draft: the dialog id, suffixed with the topic key.</summary>
    public static string ToReadModelId(string dialogId, string topicKey)
    {
        return IsChatLevel(topicKey) ? dialogId : $"{dialogId}_{topicKey}";
    }
}
