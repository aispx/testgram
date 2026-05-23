using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Start screen sharing in a call
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 403 PARTICIPANT_JOIN_MISSING Trying to enable a presentation, when the user hasn't joined the Video Chat with <a href="https://corefork.telegram.org/method/phone.joinGroupCall">phone.joinGroupCall</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.joinGroupCallPresentation"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class JoinGroupCallPresentationHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestJoinGroupCallPresentation, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestJoinGroupCallPresentation obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input.UserId);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var participant = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId);
        if (participant == null)
        {
            RpcErrors.RpcErrors403.ParticipantJoinMissing.ThrowRpcError();
            return null!;
        }

        participant.PresentationPaused = false;
        participant.PresentationParamsJson = obj.Params.Data;
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var participantsUpdate = GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant]);
        var pushUpdates = GroupCallStateHelper.Updates(participantsUpdate);
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            pushUpdates,
            input.UserId);

        return GroupCallStateHelper.Updates(
            participantsUpdate,
            GroupCallStateHelper.CreateConnectionUpdate(groupCall, options.CurrentValue.WebRtcConnections, true));
    }
}
