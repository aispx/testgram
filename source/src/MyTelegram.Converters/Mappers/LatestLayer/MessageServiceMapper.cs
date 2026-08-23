using System.Diagnostics.CodeAnalysis;

namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class MessageServiceMapper
    : IObjectMapper<IMessageReadModel, TMessageService>,
        IObjectMapper<MessageItem, TMessageService>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    /// <summary>
    /// A Telegram Passport submission is one service message with two renderings: the receiving bot
    /// gets <c>messageActionSecureValuesSentMe</c> with the encrypted documents and credentials, while
    /// the user who sent them sees <c>messageActionSecureValuesSent</c>, which only names the document
    /// types. Sending the "...SentMe" form to the user would hand their own client a payload it has no
    /// rendering for - and there is no reason to ship the credentials back to their author.
    /// See https://corefork.telegram.org/api/passport
    /// </summary>
    private static IMessageAction ForViewer(IMessageAction action, bool isOut)
    {
        if (!isOut || action is not TMessageActionSecureValuesSentMe sentMe)
        {
            return action;
        }

        var types = new TVector<ISecureValueType>();
        foreach (var value in sentMe.Values ?? [])
        {
            if (value is TSecureValue secureValue)
            {
                types.Add(secureValue.Type);
            }
        }

        return new TMessageActionSecureValuesSent { Types = types };
    }

    public TMessageService Map(IMessageReadModel source)
    {
        return Map(source, new TMessageService());
    }

    public TMessageService Map(
        IMessageReadModel source,
        TMessageService destination
    )
    {
        destination.Out = source.Out;
        //destination.Mentioned = source.Mentioned;
        //destination.MediaUnread = source.MediaUnread;
        //destination.ReactionsArePossible = source.ReactionsArePossible;
        destination.Silent = source.Silent;
        destination.Post = source.Post;
        //destination.Legacy = source.Legacy;
        destination.Id = source.MessageId;
        //destination.FromId = source.FromId;

        var peer = new Peer(source.ToPeerType, source.ToPeerId);
        destination.PeerId = peer.ToPeer();

        destination.ReplyTo = source.ReplyTo.ToMessageReplyHeader(source.ForumTopic);
        destination.Date = source.Date;
        destination.Action = ForViewer(
            source.MessageAction ?? source.MessageActionData?.ToBytes().ToTObject<IMessageAction>() ?? new TMessageActionEmpty(),
            source.Out);
        //destination.Reactions = source.Reactions;
        destination.TtlPeriod = source.TtlPeriod;

        //if (destination.Action is TMessageActionChatAddUser)
        //{
        //    destination.ReactionsArePossible = true;
        //}
        switch (destination.Action)
        {
            case TMessageActionChatAddUser:
            case TMessageActionChatEditPhoto:
            case TMessageActionChatEditTitle:
            case TMessageActionChatJoinedByLink:
            case TMessageActionChatJoinedByRequest:
            case TMessageActionSetChatWallPaper:
            case TMessageActionSetChatTheme:
                destination.ReactionsArePossible = true;
                break;
        }

        return destination;
    }

    [return: NotNullIfNotNull("source")]
    public TMessageService? Map(MessageItem source)
    {
        return Map(source, new TMessageService());
    }

    [return: NotNullIfNotNull("source")]
    public TMessageService? Map(MessageItem source, TMessageService destination)
    {
        destination.Out = source.IsOut;
        //destination.Mentioned = source.Mentioned;
        //destination.MediaUnread = source.MediaUnread;
        //destination.ReactionsArePossible = source.ReactionsArePossible;
        destination.Silent = source.Silent;
        destination.Post = source.Post; // source.Post;
        //destination.Legacy = source.Legacy;
        destination.Id = source.MessageId;
        //destination.FromId = source.FromId;

        destination.PeerId = source.ToPeer.ToPeer();

        destination.ReplyTo = source.InputReplyTo.ToMessageReplyHeader(source.ForumTopic);
        destination.Date = source.Date;
        destination.Action = ForViewer(source.MessageAction ?? new TMessageActionEmpty(), source.IsOut);
        //destination.Reactions = source.Reactions;
        destination.TtlPeriod = source.TtlPeriod;

        switch (destination.Action)
        {
            case TMessageActionChatAddUser:
            case TMessageActionChatEditPhoto:
            case TMessageActionChatEditTitle:
            case TMessageActionChatJoinedByLink:
            case TMessageActionChatJoinedByRequest:
            case TMessageActionSetChatWallPaper:
            case TMessageActionSetChatTheme:
                destination.ReactionsArePossible = true;
                break;
        }

        return destination;
    }
}