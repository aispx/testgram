using MyTelegram.Messenger.Services.HistoryImport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Upload a media file associated with an <a href="https://corefork.telegram.org/api/import">imported chat, click here for more info »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 IMPORT_ID_INVALID The specified import ID is invalid.
/// 400 MEDIA_INVALID Media invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.uploadImportedMedia"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UploadImportedMediaHandler(
    IPeerHelper peerHelper,
    IHistoryImportPeerValidator peerValidator,
    IHistoryImportStore historyImportStore,
    IMediaHelper mediaHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUploadImportedMedia, MyTelegram.Schema.IMessageMedia>
{
    protected override async Task<IMessageMedia> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestUploadImportedMedia obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // The rights are checked again on every step: an admin demoted midway through an import must
        // not be able to keep feeding files into the chat.
        await peerValidator.ValidateAsync(input.UserId, peer);

        var import = await HistoryImportAccess.LoadPendingAsync(historyImportStore, obj.ImportId, input.UserId,
            peer);

        // The name is what ties the file to the line of the export that mentions it.
        var fileName = obj.FileName?.Trim();
        if (string.IsNullOrEmpty(fileName))
        {
            RpcErrors.RpcErrors400.ImportFileInvalid.ThrowRpcError();
        }

        var media = await mediaHelper.SaveMediaAsync(obj.Media);
        if (media == null)
        {
            RpcErrors.RpcErrors400.MediaInvalid.ThrowRpcError();
        }

        await historyImportStore.SaveMediaAsync(import.Id, fileName!, media!);

        return media!;
    }
}