namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Add an existing telegram user as contact.Use <a href="https://corefork.telegram.org/method/contacts.importContacts">contacts.importContacts</a> to add contacts by phone number, without knowing their Telegram ID.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CONTACT_ID_INVALID The provided contact ID is invalid.
/// 400 CONTACT_NAME_EMPTY Contact name empty.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.addContact"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class AddContactHandler(ICommandBus commandBus, IAccessHashHelper accessHashHelper, IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestAddContact, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestAddContact obj)
    {
        var peer = obj.Id.ToPeer(input.UserId);
        switch (obj.Id)
        {
            case TInputUser inputUser:
                await accessHashHelper.CheckAccessHashAsync(input, inputUser.UserId, inputUser.AccessHash, AccessHashType.User);
                break;
            case TInputUserFromMessage inputUserFromMessage:
                await accessHashHelper.CheckAccessHashAsync(input, inputUserFromMessage.Peer);
                await ValidateSourceMessageAsync(input, inputUserFromMessage.Peer, inputUserFromMessage.MsgId);
                break;
            case TInputUserSelf:
            case TInputUserEmpty:
                RpcErrors.RpcErrors400.ContactIdInvalid.ThrowRpcError();
                break;
        }

        if (peer.PeerType != PeerType.User || peer.PeerId == input.UserId)
        {
            RpcErrors.RpcErrors400.ContactIdInvalid.ThrowRpcError();
        }

        if (string.IsNullOrWhiteSpace(obj.FirstName) && string.IsNullOrWhiteSpace(obj.LastName))
        {
            RpcErrors.RpcErrors400.ContactNameEmpty.ThrowRpcError();
        }

        // Bots and system users cannot be added as contacts (would otherwise let users
        // give bots a custom display name, which is not allowed by Telegram).
        if (await PeerKindHelper.IsBotOrSystemAsync(queryProcessor, peer.PeerId))
        {
            RpcErrors.RpcErrors400.ContactIdInvalid.ThrowRpcError();
        }

        var command = new AddContactCommand(ContactId.Create(input.UserId, peer.PeerId), input.ToRequestInfo(), input.UserId, peer.PeerId, obj.Phone, obj.FirstName, obj.LastName, obj.AddPhonePrivacyException);
        await commandBus.PublishAsync(command, default);
        return null !;
    }

    private async Task ValidateSourceMessageAsync(IRequestInput input, IInputPeer peer, int messageId)
    {
        var ownerPeerId = GetOwnerPeerId(input, peer);
        if (ownerPeerId == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId.Value, messageId).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }
    }

    private static long? GetOwnerPeerId(IRequestInput input, IInputPeer peer) =>
        peer switch
        {
            TInputPeerChannel inputPeerChannel => inputPeerChannel.ChannelId,
            TInputPeerChat inputPeerChat => inputPeerChat.ChatId,
            TInputPeerUser or TInputPeerSelf => input.UserId,
            _ => null,
        };
}
