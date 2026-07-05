using MongoDB.Driver;
using MyTelegram.Messenger.Services.PaidMedia;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch updated information about <a href="https://corefork.telegram.org/api/paid-media">paid media, see here »</a> for the full flow.This method will return an array of <a href="https://corefork.telegram.org/constructor/updateMessageExtendedMedia">updateMessageExtendedMedia</a> updates, only for messages containing <strong>already bought</strong> paid media.<br/>
/// No information will be returned for messages containing not yet bought paid media.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getExtendedMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetExtendedMediaHandler(IPeerHelper peerHelper, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetExtendedMedia, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetExtendedMedia obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var updates = new TVector<IUpdate>();

        foreach (var msgId in obj.Id.Distinct())
        {
            var context = await PaidMediaHelper.ResolveMessageAsync(mongoDatabase, peer, input.UserId, msgId);
            if (context == null || !PaidMediaHelper.IsPurchasedBy(context.Document, input.UserId))
                continue;

            updates.Add(new TUpdateMessageExtendedMedia
            {
                Peer = context.DisplayPeer.ToPeer(),
                MsgId = context.DisplayMsgId,
                ExtendedMedia = PaidMediaHelper.ToRevealedExtendedMedia(context.Document.ExtendedMedia)
            });
        }

        return new TUpdates
        {
            Updates = updates,
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };
    }
}
