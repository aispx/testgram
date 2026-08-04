using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Enable/disable <a href="https://corefork.telegram.org/api/top-rating">top peers</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.toggleTopPeers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleTopPeersHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestToggleTopPeers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestToggleTopPeers obj)
    {
        await TopPeerRatingHelper.SetDisabledAsync(mongoDatabase, input.UserId, !obj.Enabled);
        return new TBoolTrue();
    }
}
