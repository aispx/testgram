using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Create a group call or livestream
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CREATE_CALL_FAILED An error occurred while creating the call.
/// 400 GROUPCALL_ALREADY_DISCARDED The group call was already discarded.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 SCHEDULE_DATE_INVALID Invalid schedule date provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.createGroupCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CreateGroupCallHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestCreateGroupCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestCreateGroupCall obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        if (obj.ScheduleDate is { } scheduleDate && scheduleDate <= GroupCallStateHelper.CurrentDate())
        {
            RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
            return null!;
        }

        var existing = await _groupCallCollection
            .Find(g => g.CreatorId == input.UserId && g.RandomId == obj.RandomId)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(existing, input.UserId, peerHelper));
        }

        var callId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var call = new GroupCallDocument
        {
            Id = callId,
            CallId = callId,
            AccessHash = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RandomId = obj.RandomId,
            PeerId = peer.PeerId,
            PeerType = (int)peer.PeerType,
            CreatorId = input.UserId,
            Title = obj.Title,
            ScheduleDate = obj.ScheduleDate,
            RtmpStream = obj.RtmpStream,
            Date = GroupCallStateHelper.CurrentDate()
        };
        await _groupCallCollection.InsertOneAsync(call);

        return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(call, input.UserId, peerHelper));
    }
}
