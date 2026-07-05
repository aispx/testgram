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
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestEditGroupCallParticipant, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    // Participant volume is expressed in 1/100 of a percent; clients clamp it to [1, 20000] (1% - 200%).
    private const int MinParticipantVolume = 1;
    private const int MaxParticipantVolume = 20000;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestEditGroupCallParticipant obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input.UserId);
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

        if (obj.Volume.HasValue && (obj.Volume.Value < MinParticipantVolume || obj.Volume.Value > MaxParticipantVolume))
        {
            RpcErrors.RpcErrors400.UserVolumeInvalid.ThrowRpcError();
            return null!;
        }

        // A user may only raise their own hand; raising another participant's hand is forbidden.
        if (obj.RaiseHand is true &&
            !GroupCallStateHelper.IsParticipantControlledByUser(groupCall, participant, input.UserId))
        {
            RpcErrors.RpcErrors400.RaiseHandForbidden.ThrowRpcError();
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

        var updates = GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);
        return updates;
    }
}
