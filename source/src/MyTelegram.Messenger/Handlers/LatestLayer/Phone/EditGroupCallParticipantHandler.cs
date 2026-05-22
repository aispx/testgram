using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Edit information about a given group call participantNote: <a href="https://corefork.telegram.org/mtproto/TL-combinators#conditional-fields">flags</a>.N?<a href="https://corefork.telegram.org/type/Bool">Bool</a> parameters can have three possible values:
/// Possible errors
/// Code Type Description
/// 403 GROUPCALL_FORBIDDEN The group call has already ended.
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 PARTICIPANT_JOIN_MISSING Trying to enable a presentation, when the user hasn't joined the Video Chat with <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// 400 RAISE_HAND_FORBIDDEN You cannot raise your hand.
/// 400 USER_VOLUME_INVALID The specified user volume is invalid.
/// 400 VIDEO_PAUSE_FORBIDDEN You cannot pause the video stream.
/// 400 VIDEO_STOP_FORBIDDEN You cannot stop the video stream.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.editGroupCallParticipant"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class EditGroupCallParticipantHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestEditGroupCallParticipant, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestEditGroupCallParticipant obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!groupCall.Active)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(obj.Participant, input.UserId);
        var participant = peer == null
            ? null
            : groupCall.Participants.FirstOrDefault(p => p.PeerId == peer.PeerId && p.PeerType == (int)peer.PeerType);
        if (participant == null)
        {
            RpcErrors.RpcErrors400.ParticipantJoinMissing.ThrowRpcError();
            return null!;
        }

        if (obj.Muted.HasValue) participant.Muted = obj.Muted.Value;
        if (obj.Volume.HasValue) participant.Volume = obj.Volume.Value;
        if (obj.RaiseHand.HasValue) participant.RaiseHand = obj.RaiseHand.Value;
        if (obj.VideoStopped.HasValue) participant.VideoStopped = obj.VideoStopped.Value;
        if (obj.VideoPaused.HasValue) participant.VideoPaused = obj.VideoPaused.Value;
        if (obj.PresentationPaused.HasValue) participant.PresentationPaused = obj.PresentationPaused.Value;
        groupCall.Version++;

        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        return GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
    }
}
