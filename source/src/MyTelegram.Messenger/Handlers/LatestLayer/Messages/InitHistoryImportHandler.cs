using System.Text;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.HistoryImport;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Import chat history from a foreign chat app into a specific Telegram chat, <a href="https://corefork.telegram.org/api/import">click here for more info about imported chats »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 IMPORT_FILE_INVALID The specified chat export file is invalid.
/// 400 IMPORT_FORMAT_DATE_INVALID The date specified in the import file is invalid.
/// 400 IMPORT_FORMAT_UNRECOGNIZED The specified chat export file was exported from an unsupported chat app.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 406 PREVIOUS_CHAT_IMPORT_ACTIVE_WAIT_%dMIN Import for this chat is already in progress, wait %d minutes before starting a new one.
/// 400 USER_NOT_MUTUAL_CONTACT The provided user is not a mutual contact.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.initHistoryImport"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class InitHistoryImportHandler(
    IPeerHelper peerHelper,
    IHistoryImportPeerValidator peerValidator,
    IChatExportParser chatExportParser,
    IHistoryImportStore historyImportStore,
    IHistoryImportFileReader fileReader,
    IMongoDatabase database,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<InitHistoryImportHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestInitHistoryImport,
        MyTelegram.Schema.Messages.IHistoryImport>
{
    protected override async Task<MyTelegram.Schema.Messages.IHistoryImport> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestInitHistoryImport obj)
    {
        var config = options.Value.HistoryImport;
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // A basic group cannot hold an imported history: the client converts it to a supergroup after
        // messages.checkHistoryImportPeer and initializes the import on the new channel.
        await peerValidator.ValidateAsync(input.UserId, peer);

        if (obj.MediaCount < 0 || obj.MediaCount > config.MaxMediaCount)
        {
            RpcErrors.RpcErrors400.ImportFileInvalid.ThrowRpcError();
        }

        var unfinished = await historyImportStore.GetUnfinishedForPeerAsync(peer);
        if (unfinished != null)
        {
            var elapsed = DateTime.UtcNow - unfinished.CreatedAt;
            var minutesLeft = (int)Math.Ceiling(config.ActiveImportTimeoutMinutes - elapsed.TotalMinutes);
            if (minutesLeft > 0)
            {
                RpcErrors.RpcErrors406.PreviousChatImportActiveWaitXmin.ThrowRpcError(minutesLeft);
            }

            // The previous attempt is older than the window: it is abandoned, not active.
            await historyImportStore.SetStatusAsync(unfinished.Id, HistoryImportStatus.Failed);
            await historyImportStore.CleanupAsync(unfinished.Id);
        }

        var fileName = obj.File switch
        {
            TInputFile inputFile => inputFile.Name,
            TInputFileBig inputFileBig => inputFileBig.Name,
            _ => string.Empty
        };

        var bytes = await fileReader.ReadAsync(input.UserId, obj.File, fileName, config.MaxFileSizeBytes);
        if (bytes == null || bytes.Length == 0)
        {
            RpcErrors.RpcErrors400.ImportFileInvalid.ThrowRpcError();
        }

        var text = Decode(bytes!);
        ChatExportParseResult? parsed;
        try
        {
            parsed = chatExportParser.Parse(text);
        }
        catch (ChatExportDateException ex)
        {
            logger.LogWarning(ex, "Could not read a date of the chat export file of user {UserId}", input.UserId);
            RpcErrors.RpcErrors400.ImportFormatDateInvalid.ThrowRpcError();
            return null!;
        }

        if (parsed == null)
        {
            RpcErrors.RpcErrors400.ImportFormatUnrecognized.ThrowRpcError();
        }

        if (parsed!.Messages.Count == 0 || parsed.Messages.Count > config.MaxMessages)
        {
            RpcErrors.RpcErrors400.ImportFileInvalid.ThrowRpcError();
        }

        // Clients lay out a history by message id, and the ids are handed out in the order the worker
        // sends the messages. Sorting by the parsed date makes that order chronological even when the
        // export file is not: LINQ's sort is stable, so lines sharing a timestamp keep their file order.
        var messages = parsed.Messages.OrderBy(p => p.Date).ToList();

        var import = await historyImportStore.CreateAsync(input.UserId, peer, parsed.Format, obj.MediaCount,
            input.Layer, messages);

        // The export file has been read; its staged upload parts are of no use to anybody now.
        await UploadedFileReader.DeletePartsAsync(database, input.UserId, obj.File);

        logger.LogInformation(
            "History import {ImportId} initialized by user {UserId} for {PeerType} {PeerId}: {Format}, {Count} messages, {MediaCount} media",
            import.Id, input.UserId, peer.PeerType, peer.PeerId, parsed.Format, parsed.Messages.Count,
            obj.MediaCount);

        return new MyTelegram.Schema.Messages.THistoryImport { Id = import.Id };
    }

    /// <summary>
    /// Exports are plain text, but the desktop exporters of some apps still write UTF-16 with a byte
    /// order mark, which would otherwise be read as a wall of unrecognizable characters.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}