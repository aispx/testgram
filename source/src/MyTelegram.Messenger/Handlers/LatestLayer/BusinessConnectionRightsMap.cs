using MongoDB.Bson;

namespace MyTelegram.Messenger.Handlers;

/// <summary>
/// Maps each method that may be wrapped in <c>invokeWithBusinessConnection</c> to the business-bot
/// right it requires.
/// <para>
/// <c>invokeWithBusinessConnection</c> executes the inner request under the connected user's identity,
/// so without an allowlist a bot holding any single right could reach every registered constructor as
/// that user — including account and auth methods. Per
/// <a href="https://corefork.telegram.org/api/bots/connected-business-bots">the API docs</a> only a
/// fixed set of methods is wrappable, and each is governed by a specific right from
/// <c>botBusinessRights</c>; anything outside the set must fail with
/// <c>BUSINESS_CONNECTION_NOT_ALLOWED</c>.
/// </para>
/// </summary>
internal static class BusinessConnectionRightsMap
{
    /// <summary>Right names as persisted in the <c>Rights</c> subdocument of <c>connected_business_bots</c>.</summary>
    private const string Reply = "Reply";
    private const string ReadMessages = "ReadMessages";
    private const string DeleteSentMessages = "DeleteSentMessages";
    private const string EditName = "EditName";
    private const string EditBio = "EditBio";
    private const string EditUsername = "EditUsername";
    private const string ManageStories = "ManageStories";

    /// <summary>
    /// Constructor id of every wrappable method, mapped to the right it needs. A method absent from
    /// this table is not wrappable at all.
    /// </summary>
    private static readonly Dictionary<uint, string> RequiredRightByConstructorId = new()
    {
        // Sending and editing on the user's behalf — "can reply" in the UI.
        [0x545cd15a] = Reply, // messages.sendMessage
        [0x330e77f] = Reply,  // messages.sendMedia
        [0x1bf89d74] = Reply, // messages.sendMultiMedia
        [0x14967978] = Reply, // messages.uploadMedia
        [0x51e842e1] = Reply, // messages.editMessage
        [0x13704a7c] = Reply, // messages.forwardMessages
        [0x58943ee2] = Reply, // messages.setTyping
        [0xd30d78d4] = Reply, // messages.sendReaction
        [0xd2aaf7ec] = Reply, // messages.updatePinnedMessage
        [0x62dd747] = Reply,  // messages.unpinAllMessages

        // Reading the conversation.
        [0x4423e6c5] = ReadMessages, // messages.getHistory
        [0x63c66506] = ReadMessages, // messages.getMessages
        [0xe306d3a] = ReadMessages,  // messages.readHistory
        [0x9ec44f93] = ReadMessages, // messages.readReactions
        [0x8bba90e6] = ReadMessages, // messages.getMessagesReactions
        [0x461b3f48] = ReadMessages, // messages.getMessageReactionsList

        // Deleting messages the bot itself sent.
        [0xe58e95d2] = DeleteSentMessages, // messages.deleteMessages

        // Profile management — each gated on its own right, not on Reply.
        [0x78515775] = EditName,     // account.updateProfile (also covers bio; see note below)
        [0x3e0bdd7c] = EditUsername, // account.updateUsername

        // Stories posted as the connected user.
        [0x8f9e6898] = ManageStories // stories.sendStory
    };

    /// <summary>
    /// True when <paramref name="constructorId"/> may be wrapped at all. Callers must reject with
    /// <c>BUSINESS_CONNECTION_NOT_ALLOWED</c> when this is false.
    /// </summary>
    public static bool IsWrappable(uint constructorId)
    {
        return RequiredRightByConstructorId.ContainsKey(constructorId);
    }

    /// <summary>
    /// True when the connection's stored rights permit <paramref name="constructorId"/>. Returns false
    /// for a method that is not wrappable, so a caller that checks this alone still fails closed.
    /// </summary>
    /// <remarks>
    /// <c>account.updateProfile</c> can change both first/last name and bio in one call, so it is
    /// accepted when the bot holds either <c>EditName</c> or <c>EditBio</c>. Splitting that per-field
    /// would need the inner request's flags, which this table deliberately does not inspect.
    /// </remarks>
    public static bool IsAllowed(uint constructorId, BsonDocument? rights)
    {
        if (!RequiredRightByConstructorId.TryGetValue(constructorId, out var requiredRight))
        {
            return false;
        }

        if (HasRight(rights, requiredRight))
        {
            return true;
        }

        // account.updateProfile covers name and bio; EditBio is an equally valid grant for it.
        return requiredRight == EditName
               && constructorId == 0x78515775
               && HasRight(rights, EditBio);
    }

    private static bool HasRight(BsonDocument? rights, string rightName)
    {
        return rights != null
               && rights.TryGetValue(rightName, out var value)
               && value.IsBoolean
               && value.AsBoolean;
    }
}
