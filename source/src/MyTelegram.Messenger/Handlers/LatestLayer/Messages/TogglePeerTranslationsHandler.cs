namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Show or hide the <a href="https://corefork.telegram.org/api/translation">real-time chat translation popup</a> for a certain chat
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.togglePeerTranslations"/> </c></para>
/// </summary>
/// <remarks>
/// <para>The stored flag is read back as <c>translations_disabled</c> on <c>userFull</c>,
/// <c>chatFull</c> and <c>channelFull</c>, which is how the other sessions learn the popup was
/// dismissed — the API's own words. It answered <c>boolTrue</c> and stored nothing before, so the popup
/// came back on every other device and after every cache clear.</para>
///
/// <para>No update is pushed: no <c>update*</c> constructor carries this flag, and every client
/// re-reads it from the full info (Android keeps its own copy and refreshes from
/// <c>getUserFull</c>/<c>getChatFull</c>).</para>
///
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class TogglePeerTranslationsHandler(
    IPeerHelper peerHelper,
    IPeerTranslationSettingsStore store)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestTogglePeerTranslations, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestTogglePeerTranslations obj)
    {
        // Through IPeerHelper, exactly like the read path: inputPeerSelf normalises to PeerType.Self,
        // and a write that recorded it as PeerType.User would land on a row nothing reads back.
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        if (peer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await store.SetAsync(input.UserId, peer!, obj.Disabled);

        return new TBoolTrue();
    }
}
