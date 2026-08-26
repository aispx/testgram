namespace MyTelegram.Messenger.Services.Emoji;

/// <summary>
/// Translates the <c>msg_id</c> inside
/// <a href="https://corefork.telegram.org/api/animated-emojis#emoji-reactions"><c>sendMessageEmojiInteraction</c></a>
/// into the recipient's numbering before the action is relayed as <c>updateUserTyping</c>.
///
/// <para>Private chats have <b>one message id space per user</b> here, exactly as on the official
/// server: the same message is <c>MessageReadModel.MessageId</c> in its owner's box and
/// <c>SenderMessageId</c> in the sender's. The action names the message <i>the clicking user</i>
/// clicked, and the receiving client looks that id up in <i>its own</i> box
/// (tdlib <c>StickersManager::on_animated_emoji_message_clicked</c> resolves it inside the sender's
/// dialog), so relaying the id verbatim points the other side at a different message - or at none.
/// The update still arrives and nothing is logged; the reaction animation is simply never drawn.</para>
/// </summary>
public interface IEmojiInteractionMsgIdMapper
{
    /// <summary>
    /// The action to relay: the original one when it carries no message id, a copy with the
    /// recipient's id when the message could be mapped, or <c>null</c> when it could not - a click on
    /// a message the peer no longer has must drop the update rather than fail the call.
    /// </summary>
    Task<ISendMessageAction?> TranslateAsync(IRequestInput input, Peer peer, ISendMessageAction? action,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class EmojiInteractionMsgIdMapper(IQueryProcessor queryProcessor)
    : IEmojiInteractionMsgIdMapper, ITransientDependency
{
    public async Task<ISendMessageAction?> TranslateAsync(IRequestInput input, Peer peer,
        ISendMessageAction? action, CancellationToken cancellationToken = default)
    {
        if (action is not TSendMessageEmojiInteraction interaction)
        {
            return action;
        }

        if (interaction.MsgId <= 0)
        {
            return null;
        }

        // The same mapper messages.sendMessage uses to translate reply ids across a private chat: it
        // resolves the caller's own copy and returns the other side's, in both directions.
        var items = await queryProcessor.ProcessAsync(
            new GetReplyToMsgIdListQuery(peer, input.UserId, interaction.MsgId), cancellationToken);

        var target = items?.FirstOrDefault(p => p.UserId == peer.PeerId) ?? items?.FirstOrDefault();
        if (target == null || target.MessageId <= 0)
        {
            return null;
        }

        if (target.MessageId == interaction.MsgId)
        {
            return interaction;
        }

        return new TSendMessageEmojiInteraction
        {
            Emoticon = interaction.Emoticon,
            MsgId = target.MessageId,
            Interaction = interaction.Interaction
        };
    }
}
