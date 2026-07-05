using MongoDB.Driver;
using System.Text;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Edit/create a <a href="https://corefork.telegram.org/api/factcheck">fact-check</a> on a message.Can only be used by independent fact-checkers as specified by the <a href="https://corefork.telegram.org/api/config#can-edit-factcheck">appConfig.can_edit_factcheck</a> configuration flag.
/// Possible errors
/// Code Type Description
/// 403 CHAT_ACTION_FORBIDDEN You cannot execute this action.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.editFactCheck"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditFactCheckHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IMessageConverterService messageConverterService,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestEditFactCheck, MyTelegram.Schema.IUpdates>
{
    private const int FactCheckLengthLimit = 1024;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestEditFactCheck obj)
    {
        if (!await FactCheckHelper.CanEditFactCheckAsync(mongoDatabase, input.UserId))
        {
            RpcErrors.RpcErrors403.ChatActionForbidden.ThrowRpcError();
        }

        var plainText = FactCheckHelper.ExtractPlainText(obj.Text);
        if (Encoding.UTF8.GetByteCount(plainText) > FactCheckLengthLimit)
        {
            RpcErrors.RpcErrors400.InputTextTooLong.ThrowRpcError();
        }

        var (ownerPeerId, messageReadModel) = await GetMessageAsync(input, obj.Peer, obj.MsgId);
        var doc = await FactCheckHelper.UpsertAsync(mongoDatabase, ownerPeerId, obj.MsgId, obj.Text, input.UserId, CurrentDate);
        return ToUpdates(input, messageReadModel, FactCheckHelper.ToFactCheck(doc, needCheck: false));
    }

    private async Task<(long OwnerPeerId, IMessageReadModel Message)> GetMessageAsync(IRequestInput input, IInputPeer inputPeer, int messageId)
    {
        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var messageReadModel = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(ownerPeerId, messageId).Value));
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        return (ownerPeerId, messageReadModel!);
    }

    private IUpdates ToUpdates(IRequestInput input, IMessageReadModel messageReadModel, IFactCheck factCheck)
    {
        var message = messageConverterService.ToMessage(input.UserId, messageReadModel, layer: input.Layer);
        if (message is not TMessage tMessage)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return new TUpdates { Updates = [], Users = [], Chats = [], Date = CurrentDate };
        }

        tMessage.Factcheck = factCheck;
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
