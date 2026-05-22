using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Rate a call, returns info about the rating message sent to the official VoIP bot.
/// Possible errors
/// Code Type Description
/// 400 CALL_PEER_INVALID The provided call peer object is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.setCallRating"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SetCallRatingHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestSetCallRating, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<CallSessionDocument> _callCollection =
        mongoDatabase.GetCollection<CallSessionDocument>("call_sessions");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestSetCallRating obj)
    {
        if (obj.Peer is not TInputPhoneCall inputPhoneCall)
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        var filter = Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, inputPhoneCall.Id);
        var session = await _callCollection.Find(filter).FirstOrDefaultAsync();
        if (session == null ||
            (session.AccessHash != inputPhoneCall.AccessHash &&
             !await accessHashHelper2.IsAccessHashValidAsync(input, inputPhoneCall.Id, inputPhoneCall.AccessHash, AccessHashType.Call)) ||
            (session.CallerId != input.UserId && session.CalleeId != input.UserId) ||
            session.State != "discarded")
        {
            RpcErrors.RpcErrors400.CallPeerInvalid.ThrowRpcError();
            return null!;
        }

        await _callCollection.UpdateOneAsync(filter,
            Builders<CallSessionDocument>.Update
                .Set(s => s.Rating, obj.Rating)
                .Set(s => s.RatingComment, obj.Comment)
                .Set(s => s.RatingUserInitiative, obj.UserInitiative));

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }
}
