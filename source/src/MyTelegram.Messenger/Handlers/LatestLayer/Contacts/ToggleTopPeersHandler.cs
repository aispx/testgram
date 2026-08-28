namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Enable/disable <a href="https://corefork.telegram.org/api/top-rating">top peers</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.toggleTopPeers"/> </c></para>
/// </summary>
/// <remarks>
/// There is no update for this in the schema — a client learns the new state from the next
/// <c>contacts.getTopPeers</c>, which answers <c>contacts.topPeersDisabled</c> and makes tdlib set
/// <c>disable_top_chats</c> and Android clear its hints table. Disabling also stops the server counting,
/// so re-enabling does not surface history the user believed was not being kept.
/// <para>Access: [User ✔] [Bot ✖] [Anonymous ✖]</para>
/// </remarks>
internal sealed class ToggleTopPeersHandler(ITopPeerRatingService ratingService)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestToggleTopPeers, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestToggleTopPeers obj)
    {
        await ratingService.SetDisabledAsync(input.UserId, !obj.Enabled);

        return new TBoolTrue();
    }
}
