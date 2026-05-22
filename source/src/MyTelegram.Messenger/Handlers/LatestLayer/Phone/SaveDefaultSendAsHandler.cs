using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.Phone;
/// <summary>
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.saveDefaultSendAs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveDefaultSendAsHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSaveDefaultSendAs, IBool>, IObjectHandler
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSaveDefaultSendAs obj)
    {
        if (obj.Call is not TInputGroupCall inputGroupCall)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var filter = GroupCallStateHelper.Filter(inputGroupCall);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        var sendAs = peerHelper.GetPeer(obj.SendAs, input.UserId);
        if (groupCall == null || sendAs == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        groupCall.DefaultSendAsPeerId = sendAs.PeerId;
        groupCall.DefaultSendAsPeerType = (int)sendAs.PeerType;
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);
        return new TBoolTrue();
    }
}
