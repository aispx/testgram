using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Delete a <a href="https://corefork.telegram.org/api/factcheck">fact-check</a> from a message.Can only be used by independent fact-checkers as specified by the <a href="https://corefork.telegram.org/api/config#can-edit-factcheck">appConfig.can_edit_factcheck</a> configuration flag.
/// Possible errors
/// Code Type Description
/// 403 CHAT_ACTION_FORBIDDEN You cannot execute this action.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.deleteFactCheck"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteFactCheckHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMessageConverterService messageConverterService,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestDeleteFactCheck, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestDeleteFactCheck obj)
    {
        if (!await FactCheckHelper.CanEditFactCheckAsync(mongoDatabase, input.UserId))
        {
            RpcErrors.RpcErrors403.ChatActionForbidden.ThrowRpcError();
        }

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, obj.MsgId).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        await FactCheckHelper.DeleteAsync(mongoDatabase, ownerPeerId, obj.MsgId);
        return ToUpdates(input, messageReadModel!);
    }

    private IUpdates ToUpdates(IRequestInput input, IMessageReadModel messageReadModel)
    {
        var message = messageConverterService.ToMessage(input.UserId, messageReadModel, layer: input.Layer);
        if (message is not TMessage tMessage)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return new TUpdates { Updates = [], Users = [], Chats = [], Date = CurrentDate };
        }

        tMessage.Factcheck = null;
        IUpdate update = messageReadModel.ToPeerType == PeerType.Channel
            ? new TUpdateEditChannelMessage { Message = tMessage, Pts = messageReadModel.Pts, PtsCount = 1 }
            : new TUpdateEditMessage { Message = tMessage, Pts = messageReadModel.Pts, PtsCount = 1 };

        return new TUpdates
        {
            Updates = [update],
            Users = [],
            Chats = [],
            Date = CurrentDate,
        };
    }
}
