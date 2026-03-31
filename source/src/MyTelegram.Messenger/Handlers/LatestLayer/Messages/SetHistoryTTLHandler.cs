using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Messenger.Services;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

// Fixed: InvalidCastException when disabling auto-delete (Period=0)
namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

public class SetHistoryTTLHandler : RpcResultObjectHandler<RequestSetHistoryTTL, IUpdates>
{
    private readonly IPeerHelper _peerHelper;
    private readonly IMongoDatabase _mongoDatabase;
    private readonly IMessageAppService _messageAppService;

    public SetHistoryTTLHandler(
        IPeerHelper peerHelper,
        IMongoDatabase mongoDatabase,
        IMessageAppService messageAppService)
    {
        _peerHelper = peerHelper;
        _mongoDatabase = mongoDatabase;
        _messageAppService = messageAppService;
    }

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSetHistoryTTL obj)
    {
        var peer = obj.Peer;
        long peerId;
        PeerType peerType;

        if (peer is TInputPeerChannel inputPeerChannel)
        {
            peerId = inputPeerChannel.ChannelId;
            peerType = PeerType.Channel;
        }
        else if (peer is TInputPeerUser inputPeerUser)
        {
            peerId = inputPeerUser.UserId;
            peerType = PeerType.User;
        }
        else if (peer is TInputPeerChat inputPeerChat)
        {
            peerId = inputPeerChat.ChatId;
            peerType = PeerType.Chat;
        }
        else
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var period = obj.Period;
        if (period < 0 || period > 31536000)
        {
            RpcErrors.RpcErrors400.TtlPeriodInvalid.ThrowRpcError();
        }

        var dialogId = DialogId.Create(input.UserId, peerType, peerId);
        var dialogCollection = _mongoDatabase.GetCollection<DialogReadModel>("eventflow-dialogreadmodel");
        var filter = Builders<DialogReadModel>.Filter.Eq(d => d.Id, dialogId.Value);
        var dialog = await dialogCollection.Find(filter).FirstOrDefaultAsync();

        if (dialog != null && dialog.TtlPeriod == period)
        {
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();
        }

        var update = Builders<DialogReadModel>.Update.Set(d => d.TtlPeriod, period);
        await dialogCollection.UpdateOneAsync(filter, update);

        var action = new TMessageActionSetMessagesTTL { Period = period };
        var toPeer = new Peer(peerType, peerId);

        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            toPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );
        await _messageAppService.SendMessageAsync([sendInput]);

        // Create proper Peer object for the update
        IPeer updatePeer;
        if (peerType == PeerType.User)
        {
            updatePeer = new TPeerUser { UserId = peerId };
        }
        else if (peerType == PeerType.Chat)
        {
            updatePeer = new TPeerChat { ChatId = peerId };
        }
        else // Channel
        {
            updatePeer = new TPeerChannel { ChannelId = peerId };
        }

        var updatePeerHistoryTTL = new TUpdatePeerHistoryTTL
        {
            Peer = updatePeer,
            TtlPeriod = period
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate> { updatePeerHistoryTTL },
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}