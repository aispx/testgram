using MyTelegram.Messenger.Services.Phone;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Declines a conference call invite.
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.declineConferenceCallInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeclineConferenceCallInviteHandler(
    IMongoDatabase mongoDatabase,
    IPtsHelper ptsHelper,
    IObjectMessageSender objectMessageSender) : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDeclineConferenceCallInvite, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDeclineConferenceCallInvite obj)
    {
        if (obj.MsgId <= 0)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(new MyTelegram.Schema.TInputGroupCallInviteMessage { MsgId = obj.MsgId }, input.UserId);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return null!;
        }

        var invite = groupCall.InviteMessages.FirstOrDefault(message => message.UserId == input.UserId && message.MessageId == obj.MsgId);
        if (invite == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
            return null!;
        }

        invite.Declined = true;
        groupCall.InvitedUserIds.Remove(input.UserId);
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var pts = await ptsHelper.IncrementPtsAsync(input.UserId, ptsHelper.GetCachedPts(input.UserId));
        var selfUpdates = GroupCallStateHelper.Updates(new MyTelegram.Schema.TUpdateEditMessage
        {
            Pts = pts,
            PtsCount = 1,
            Message = new MyTelegram.Schema.TMessageService
            {
                Id = obj.MsgId,
                FromId = new MyTelegram.Schema.TPeerUser { UserId = invite.FromUserId },
                PeerId = new MyTelegram.Schema.TPeerUser { UserId = invite.FromUserId },
                Date = invite.Date,
                Action = new MyTelegram.Schema.TMessageActionConferenceCall
                {
                    CallId = groupCall.CallId,
                    Missed = true,
                    Video = invite.Video
                }
            }
        });

        var inviterUpdates = GroupCallStateHelper.Updates(new MyTelegram.Schema.TUpdateEditMessage
        {
            Pts = 0,
            PtsCount = 0,
            Message = new MyTelegram.Schema.TMessageService
            {
                Id = obj.MsgId,
                FromId = new MyTelegram.Schema.TPeerUser { UserId = invite.FromUserId },
                PeerId = new MyTelegram.Schema.TPeerUser { UserId = input.UserId },
                Date = invite.Date,
                Out = true,
                Action = new MyTelegram.Schema.TMessageActionConferenceCall
                {
                    CallId = groupCall.CallId,
                    Missed = true,
                    Video = invite.Video
                }
            }
        });
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, invite.FromUserId), inviterUpdates);

        return selfUpdates;
    }
}
