using MyTelegram.Converters.Mappers.LatestLayer;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Passport;

/// <summary>
/// Feature: how a Telegram Passport submission is rendered on each side of the chat.
///
/// <para>
/// <c>account.acceptAuthorization</c> produces a single service message. The bot must receive
/// <c>messageActionSecureValuesSentMe</c>, with the encrypted values and the credentials; the user who
/// sent them must see <c>messageActionSecureValuesSent</c>, which only names the document types.
/// See https://corefork.telegram.org/api/passport#receiving-information
/// </para>
/// </summary>
public class SecureValuesSentRenderingTests
{
    [Fact]
    public void The_bot_receives_the_values_and_the_credentials()
    {
        var message = new MessageServiceMapper().Map(Item(isOut: false));

        var action = message.Action.ShouldBeOfType<TMessageActionSecureValuesSentMe>();
        action.Values.Count.ShouldBe(2);
        action.Credentials.ShouldBeOfType<TSecureCredentialsEncrypted>();
    }

    [Fact]
    public void The_sender_only_sees_the_document_types()
    {
        var message = new MessageServiceMapper().Map(Item(isOut: true));

        // Shipping the credentials back to their author is pointless, and no user client has a
        // rendering for the "...SentMe" constructor.
        var action = message.Action.ShouldBeOfType<TMessageActionSecureValuesSent>();
        action.Types.Select(t => t.ConstructorId)
            .ShouldBe([0x9d2a81e3u, 0x3dac6a00u]);
    }

    [Fact]
    public void Any_other_action_is_left_alone()
    {
        var item = Item(isOut: true) with { MessageAction = new TMessageActionSetMessagesTTL { Period = 60 } };

        new MessageServiceMapper().Map(item).Action.ShouldBeOfType<TMessageActionSetMessagesTTL>();
    }

    private static MessageItem Item(bool isOut)
    {
        return new MessageItem(
            OwnerPeer: new Peer(PeerType.User, 2010001),
            ToPeer: new Peer(PeerType.User, 2010002),
            SenderPeer: new Peer(PeerType.User, 2010001),
            SenderUserId: 2010001,
            MessageId: 1,
            Message: string.Empty,
            Date: 0,
            RandomId: 0,
            IsOut: isOut,
            SendMessageType: SendMessageType.MessageService,
            MessageAction: new TMessageActionSecureValuesSentMe
            {
                Values = new TVector<ISecureValue>(
                    new TSecureValue { Type = new TSecureValueTypePersonalDetails(), Hash = new byte[32] },
                    new TSecureValue { Type = new TSecureValueTypePassport(), Hash = new byte[32] }),
                Credentials = new TSecureCredentialsEncrypted
                {
                    Data = new byte[16], Hash = new byte[32], Secret = new byte[256]
                }
            });
    }
}
