using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class JoinGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<RequestJoinGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestJoinGroupCall obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var joinAs = peerHelper.GetPeer(obj.JoinAs, input.UserId);
        if (joinAs == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = GroupCallStateHelper.CurrentDate();
        var ssrc = GroupCallStateHelper.CreateParticipantSource(
            obj.Params?.Data,
            groupCall.Participants,
            joinAs.PeerId,
            (int)joinAs.PeerType);

        var participant = new GroupCallParticipantDoc
        {
            PeerId = joinAs.PeerId,
            PeerType = (int)joinAs.PeerType,
            Source = ssrc,
            Muted = obj.Muted || groupCall.JoinMuted,
            VideoStopped = obj.VideoStopped,
            Date = currentDate,
            ParamsJson = obj.Params?.Data,
            PublicKey = obj.PublicKey?.ToArray()
        };

        groupCall.Participants.RemoveAll(p => p.PeerId == participant.PeerId && p.PeerType == participant.PeerType);
        groupCall.Participants.Add(participant);
        groupCall.Version++;
        if (obj.Block is { } block)
        {
            groupCall.ChainBlocks.Add(new GroupCallChainBlockDoc { Block = block.ToArray() });
        }

        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        return GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant], true));
    }
}
