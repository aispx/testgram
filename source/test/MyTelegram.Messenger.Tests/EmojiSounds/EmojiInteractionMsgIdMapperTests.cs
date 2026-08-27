using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Services.Emoji;
using MyTelegram.Queries;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.EmojiSounds;

/// <summary>
/// Tests for <see cref="EmojiInteractionMsgIdMapper"/>, which translates
/// <c>sendMessageEmojiInteraction.msg_id</c> into the recipient's numbering before
/// <c>messages.setTyping</c> relays it (https://corefork.telegram.org/api/animated-emojis#emoji-reactions).
///
/// <para>Private chats keep one id space per participant here, so the id the clicking user sends names
/// a different message — or none — in the other user's box. Relaying it unchanged delivers a perfectly
/// valid-looking <c>updateUserTyping</c> that the receiving client resolves to the wrong message and
/// then draws nothing, with nothing logged on either side.</para>
/// </summary>
public class EmojiInteractionMsgIdMapperTests
{
    private static readonly Peer Peer = new(PeerType.User, 2010002);

    [Fact]
    public async Task A_plain_typing_action_is_relayed_untouched()
    {
        var action = new TSendMessageTypingAction();

        var result = await Mapper(null).TranslateAsync(Request(), Peer, action);

        result.ShouldBeSameAs(action);
    }

    [Fact]
    public async Task Null_is_relayed_untouched()
    {
        // messages.setTyping carries a required action, but nothing here should turn a malformed
        // request into a mapping attempt.
        (await Mapper(null).TranslateAsync(Request(), Peer, null)).ShouldBeNull();
    }

    [Fact]
    public async Task The_recipients_own_message_id_is_used()
    {
        var result = await Mapper([new ReplyToMsgItem(Peer.PeerId, 42)])
            .TranslateAsync(Request(), Peer, Interaction(7));

        var interaction = result.ShouldBeOfType<TSendMessageEmojiInteraction>();
        interaction.MsgId.ShouldBe(42);
        interaction.Emoticon.ShouldBe("❤");
        interaction.Interaction.ShouldBeOfType<TDataJSON>().Data.ShouldBe("{\"v\":1}");
    }

    [Fact]
    public async Task The_original_action_is_not_mutated()
    {
        // obj.Action belongs to the deserialized request; the update is pushed to another session and
        // must not rewrite what the caller sent.
        var action = Interaction(7);

        await Mapper([new ReplyToMsgItem(Peer.PeerId, 42)]).TranslateAsync(Request(), Peer, action);

        action.MsgId.ShouldBe(7);
    }

    [Fact]
    public async Task The_recipients_copy_is_preferred_over_any_other_match()
    {
        var result = await Mapper([new ReplyToMsgItem(999, 11), new ReplyToMsgItem(Peer.PeerId, 42)])
            .TranslateAsync(Request(), Peer, Interaction(7));

        result.ShouldBeOfType<TSendMessageEmojiInteraction>().MsgId.ShouldBe(42);
    }

    [Fact]
    public async Task An_unmappable_message_drops_the_update()
    {
        // A click on a message the peer has deleted must not fail messages.setTyping, but relaying the
        // sender's id would point the other side at an unrelated message.
        (await Mapper([]).TranslateAsync(Request(), Peer, Interaction(7))).ShouldBeNull();
        (await Mapper(null).TranslateAsync(Request(), Peer, Interaction(7))).ShouldBeNull();
    }

    [Fact]
    public async Task A_missing_message_id_drops_the_update()
    {
        (await Mapper(null).TranslateAsync(Request(), Peer, Interaction(0))).ShouldBeNull();
    }

    private static TSendMessageEmojiInteraction Interaction(int msgId) => new()
    {
        Emoticon = "❤",
        MsgId = msgId,
        Interaction = new TDataJSON { Data = "{\"v\":1}" }
    };

    private static IRequestInput Request(long userId = 2010001)
    {
        var request = new Mock<IRequestInput>();
        request.SetupGet(p => p.UserId).Returns(userId);

        return request.Object;
    }

    private static EmojiInteractionMsgIdMapper Mapper(IReadOnlyCollection<ReplyToMsgItem>? items)
    {
        var queryProcessor = new Mock<IQueryProcessor>();
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetReplyToMsgIdListQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        return new EmojiInteractionMsgIdMapper(queryProcessor.Object);
    }
}
