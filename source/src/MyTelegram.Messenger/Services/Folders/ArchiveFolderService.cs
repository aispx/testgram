namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// The <c>dialogFolder</c> row of the chat archive.
///
/// <para>It is only served when the archive is pinned to the top of the main list: the live service sends no
/// <c>dialogFolder</c> at all for an unpinned archive (measured against it with 8 archived chats), and Android
/// builds the row locally in that case (<c>MessagesController.ensureFolderDialogExists</c>). Clients read only
/// <c>folder.id</c> out of it and localise the title themselves.</para>
/// See https://corefork.telegram.org/api/folders#peer-folders
/// </summary>
public interface IArchiveFolderService
{
    /// <summary>
    /// The archive row for the user's main dialog list, or <c>null</c> when the archive is not pinned.
    /// </summary>
    Task<TDialogFolder?> GetPinnedArchiveDialogAsync(long userId);
}

/// <inheritdoc />
public class ArchiveFolderService(IQueryProcessor queryProcessor) : IArchiveFolderService, ITransientDependency
{
    /// <summary>Enough to count every archived chat of a normal account in one query.</summary>
    private const int ArchivedDialogScanLimit = 1000;

    public async Task<TDialogFolder?> GetPinnedArchiveDialogAsync(long userId)
    {
        var settings = await queryProcessor.ProcessAsync(new GetDialogFilterSettingsQuery(userId));
        if (settings?.ArchivePinned != true)
        {
            return null;
        }

        var archived = await queryProcessor.ProcessAsync(new GetDialogsQuery(userId, null, null,
            new OffsetInfo(), ArchivedDialogScanLimit, null, MyTelegramConsts.ArchiveFolderId));

        var currentDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var unreadMutedPeers = 0;
        var unreadUnmutedPeers = 0;
        var unreadMutedMessages = 0;
        var unreadUnmutedMessages = 0;
        var topMessage = 0;

        foreach (var dialog in archived)
        {
            topMessage = Math.Max(topMessage, dialog.TopMessage);

            var unreadCount = dialog.UnreadCount;
            if (unreadCount <= 0)
            {
                continue;
            }

            // Muted for good or muted until a moment still ahead of us.
            var muted = dialog.NotifySettings?.MuteUntil > currentDate;
            if (muted)
            {
                unreadMutedPeers++;
                unreadMutedMessages += unreadCount;
            }
            else
            {
                unreadUnmutedPeers++;
                unreadUnmutedMessages += unreadCount;
            }
        }

        return new TDialogFolder
        {
            Pinned = true,
            Folder = new TFolder
            {
                Id = MyTelegramConsts.ArchiveFolderId,
                Title = "Archived chats"
            },
            // Clients derive the dialog id from folder.id and ignore this peer; Android puts an empty
            // peerUser here when it builds the row itself.
            Peer = new TPeerUser { UserId = userId },
            TopMessage = topMessage,
            UnreadMutedPeersCount = unreadMutedPeers,
            UnreadUnmutedPeersCount = unreadUnmutedPeers,
            UnreadMutedMessagesCount = unreadMutedMessages,
            UnreadUnmutedMessagesCount = unreadUnmutedMessages
        };
    }
}
