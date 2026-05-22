using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Remove participants from a conference call.Exactly one of the <code>only_left</code> and <code>kick</code> flags must be set.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.deleteConferenceCallParticipants"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteConferenceCallParticipantsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestDeleteConferenceCallParticipants, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestDeleteConferenceCallParticipants obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Conference)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var removed = groupCall.Participants.Where(p => obj.Ids.Contains(p.PeerId)).ToList();
        groupCall.Participants.RemoveAll(p => obj.Ids.Contains(p.PeerId));
        groupCall.ChainBlocks.Add(new GroupCallChainBlockDoc { Block = obj.Block.ToArray() });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        return GroupCallStateHelper.Updates(
            GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, removed),
            new TUpdateGroupCallChainBlocks
            {
                Call = GroupCallStateHelper.ToInputGroupCall(groupCall),
                Blocks = new TVector<ReadOnlyMemory<byte>>([obj.Block]),
                NextOffset = groupCall.ChainBlocks.Count,
                SubChainId = 0
            });
    }
}
