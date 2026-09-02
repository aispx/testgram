namespace MyTelegram.Messenger.Handlers.LatestLayer.LayerN.Account;

/// <summary>
/// Create and upload a new <a href="https://corefork.telegram.org/api/wallpapers">wallpaper</a>.
///
/// <para>Serves <c>account.uploadWallPaper#e39a8f03</c> — the layer 224 constructor, sent by Telethon and
/// tdesktop — by forwarding it to the handler written against the older <c>#dd853661</c> the generated
/// schema carries. Without this the newer form is answered by nothing at all and the client hangs.</para>
///
/// <para>See <a href="https://corefork.telegram.org/method/account.uploadWallPaper" /></para>
/// </summary>
internal sealed class UploadWallPaperHandler(
    IHandlerHelper handlerHelper,
    IRequestConverter<MyTelegram.Schema.Account.LayerN.RequestUploadWallPaper,
        MyTelegram.Schema.Account.RequestUploadWallPaper> dataConverter)
    : ForwardRequestToNewHandler<
            MyTelegram.Schema.Account.LayerN.RequestUploadWallPaper,
            MyTelegram.Schema.Account.RequestUploadWallPaper
        >(handlerHelper, dataConverter)
{
}
