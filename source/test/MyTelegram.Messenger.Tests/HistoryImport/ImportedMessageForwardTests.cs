using System.Reflection;
using Moq;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages — forwarding one of them out of the chat it was imported into.
///
/// <para>
/// The <c>imported</c> flag belongs to the imported message itself: a client that sees it stops
/// drawing the forward header altogether and shows the "imported" marker instead. Copying the flag
/// into the forward therefore hid the fact that the message was forwarded at all. The official server
/// answers a plain forward from a hidden sender, keeping the original name and date and reporting the
/// chat importer account in <c>saved_from_id</c>.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class ImportedMessageForwardTests
{
    private const long ChannelId = 1000001;
    private const int SourceMessageId = 42;

    [Fact]
    public void Forwarding_an_imported_message_keeps_the_original_author_and_hides_the_sender()
    {
        var header = BuildForwardHeader(ImportedMessage());

        header.Imported.ShouldBeFalse();
        header.FromName.ShouldBe("John Doe");
        header.Date.ShouldBe(1609459140);
        header.FromId.ShouldBeNull();
    }

    [Fact]
    public void The_forward_points_back_at_the_chat_it_was_imported_into()
    {
        var header = BuildForwardHeader(ImportedMessage());

        header.SavedFromPeer!.PeerId.ShouldBe(ChannelId);
        header.SavedFromMsgId.ShouldBe(SourceMessageId);
        header.SavedFromId!.PeerId.ShouldBe(MyTelegramConsts.ChatImporterBotUserId);
        header.SavedDate.ShouldBe(1700000000);
    }

    [Fact]
    public void The_saga_shapes_the_header_of_a_forward_the_same_way()
    {
        // The forward saga rebuilds the header from scratch, so the rule lives in one place and both
        // paths call it: the handler alone left the saga answering the sender of the imported message.
        var fwd = new MessageFwdHeader
        {
            Imported = false,
            FromId = new Peer(PeerType.User, MyTelegramConsts.ChatImporterBotUserId),
            Date = 1700000000
        };

        var applied = MessageFwdHeaderRules.TryApplyImportedOrigin(fwd, new MessageFwdHeader
        {
            Imported = true,
            FromName = "John Doe",
            Date = 1609459140
        }, new Peer(PeerType.User, MyTelegramConsts.ChatImporterBotUserId));

        applied.ShouldBeTrue();
        fwd.Imported.ShouldBeFalse();
        fwd.FromId.ShouldBeNull();
        fwd.FromName.ShouldBe("John Doe");
        fwd.Date.ShouldBe(1609459140);
        fwd.SavedFromId!.PeerId.ShouldBe(MyTelegramConsts.ChatImporterBotUserId);
    }

    [Fact]
    public void A_message_that_was_not_imported_leaves_the_header_alone()
    {
        var fwd = new MessageFwdHeader { FromName = "Anonymised", Date = 1700000000 };

        MessageFwdHeaderRules.TryApplyImportedOrigin(fwd, null, new Peer(PeerType.User, 2010001))
            .ShouldBeFalse();
        fwd.FromName.ShouldBe("Anonymised");
        fwd.Date.ShouldBe(1700000000);
    }

    [Fact]
    public void A_normal_forwarded_message_still_keeps_its_own_header()
    {
        var source = new MessageFwdHeader
        {
            Imported = false,
            FromId = new Peer(PeerType.User, 2010009),
            Date = 1609459140
        };

        var header = BuildForwardHeader(ImportedMessage(source));

        header.FromId!.PeerId.ShouldBe(2010009);
        header.SavedFromId.ShouldBeNull();
    }

    private static IMessageReadModel ImportedMessage(MessageFwdHeader? fwdHeader = null)
    {
        var message = new Mock<IMessageReadModel>(MockBehavior.Loose);
        message.SetupGet(p => p.FwdHeader).Returns(fwdHeader ?? new MessageFwdHeader
        {
            Imported = true,
            FromName = "John Doe",
            Date = 1609459140
        });
        message.SetupGet(p => p.SenderPeerId).Returns(MyTelegramConsts.ChatImporterBotUserId);
        message.SetupGet(p => p.Date).Returns(1700000000);
        message.SetupGet(p => p.ToPeerType).Returns(PeerType.Channel);
        message.SetupGet(p => p.ToPeerId).Returns(ChannelId);

        return message.Object;
    }

    private static MessageFwdHeader BuildForwardHeader(IMessageReadModel message)
    {
        var type = typeof(ChatExportParser).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Messages.ForwardMessagesHandler", throwOnError: true)!;
        var method = type.GetMethod("BuildForwardHeader", BindingFlags.Static | BindingFlags.NonPublic)!;

        return (MessageFwdHeader)method.Invoke(null,
            [new Peer(PeerType.Channel, ChannelId), message, SourceMessageId])!;
    }
}
