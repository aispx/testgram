namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns the current user dialog list.
/// Possible errors
/// Code Type Description
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 FOLDER_ID_INVALID Invalid folder ID.
/// 400 OFFSET_PEER_ID_INVALID The provided offset peer is invalid.
/// 400 PINNED_DIALOGS_TOO_MUCH Too many pinned dialogs.
/// 400 TAKEOUT_INVALID The specified takeout ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDialogs"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>An absent <c>folder_id</c> means the main list, exactly as <c>folder_id = 0</c> does — measured against
/// the live service, whose two answers are identical and whose archived chats appear only under
/// <c>folder_id = 1</c>. A pinned archive is prepended as a <c>dialogFolder</c>; an unpinned one is not
/// announced at all, again matching what the live service sends.</para>
/// </remarks>
internal sealed class GetDialogsHandler(
    IDialogAppService dialogAppService,
    IPeerHelper peerHelper,
    IArchiveFolderService archiveFolderService,
    IDialogConverterService dialogConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetDialogs, MyTelegram.Schema.Messages.IDialogs>
{
    protected override async Task<IDialogs> HandleCoreAsync(IRequestInput input, RequestGetDialogs obj)
    {
        var userId = input.UserId;
        var offsetPeer = peerHelper.GetPeer(obj.OffsetPeer);
        bool? pinned = null;
        if (obj.ExcludePinned)
        {
            pinned = false;
        }

        var getDialogOutput = await dialogAppService.GetDialogsAsync(new GetDialogInput
        {
            FolderId = obj.FolderId,
            Limit = obj.Limit,
            Pinned = pinned, //Pinned = !obj.ExcludePinned,
                             //ExcludePinned = obj.ExcludePinned,
            OwnerId = userId,
            OffsetPeer = offsetPeer
        });
        var dialogs = dialogConverterService.ToDialogs(input, getDialogOutput, input.Layer);

        if ((obj.FolderId ?? 0) == 0 && !obj.ExcludePinned)
        {
            var archive = await archiveFolderService.GetPinnedArchiveDialogAsync(userId);
            if (archive != null)
            {
                switch (dialogs)
                {
                    case TDialogs allDialogs:
                        allDialogs.Dialogs.Insert(0, archive);
                        break;
                    case TDialogsSlice slice:
                        slice.Dialogs.Insert(0, archive);
                        slice.Count++;
                        break;
                }
            }
        }

        return dialogs;
    }
}