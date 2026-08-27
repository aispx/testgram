namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get pinned dialogs
/// Possible errors
/// Code Type Description
/// 400 FOLDER_ID_INVALID Invalid folder ID.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPinnedDialogs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>A pinned chat archive is part of the pinned list of folder 0, and this is where Android expects to see
/// it: after an <c>updatePinnedDialogs</c> it re-reads the folder here and checks whether the first entry is a
/// <c>dialogFolder</c>.</para>
/// </remarks>
internal sealed class GetPinnedDialogsHandler(
    IDialogAppService dialogAppService,
    IPtsHelper ptsHelper,
    IArchiveFolderService archiveFolderService,
    IDialogConverterService dialogConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPinnedDialogs, MyTelegram.Schema.Messages.IPeerDialogs>
{
    protected override async Task<IPeerDialogs> HandleCoreAsync(IRequestInput input, RequestGetPinnedDialogs obj)
    {
        var userId = input.UserId;
        var getDialogOutput = await dialogAppService.GetDialogsAsync(new GetDialogInput { Pinned = true, OwnerId = userId, Limit = DefaultPageSize, FolderId = obj.FolderId });
        var cachedPts = ptsHelper.GetCachedPts(input.UserId);
        getDialogOutput.CachedPts = cachedPts;
        var peerDialogs = dialogConverterService.ToPeerDialogs(input, getDialogOutput, input.Layer);

        if (obj.FolderId == 0)
        {
            var archive = await archiveFolderService.GetPinnedArchiveDialogAsync(userId);
            if (archive != null)
            {
                peerDialogs.Dialogs.Insert(0, archive);
            }
        }

        return peerDialogs;
    }
}