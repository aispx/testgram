using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch one or more <a href="https://corefork.telegram.org/api/factcheck">factchecks, see here »</a> for the full flow.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getFactCheck"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetFactCheckHandler(
    IPeerHelper peerHelper,
    IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetFactCheck, TVector<MyTelegram.Schema.IFactCheck>>
{
    protected override async Task<TVector<MyTelegram.Schema.IFactCheck>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetFactCheck obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var docs = await FactCheckHelper.FindManyAsync(mongoDatabase, ownerPeerId, obj.MsgId.ToList());
        var byMessageId = docs.ToDictionary(k => k.GetValue("MessageId", 0).ToInt32());
        var result = new TVector<IFactCheck>();
        foreach (var messageId in obj.MsgId)
        {
            if (byMessageId.TryGetValue(messageId, out var doc))
            {
                result.Add(FactCheckHelper.ToFactCheck(doc, needCheck: false));
            }
        }

        return result;
    }
}
