using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Services.HistoryImport;

/// <summary>
/// Injects the parsed messages of an import into the destination chat.
/// See https://corefork.telegram.org/api/import
/// </summary>
public interface IHistoryImportRunner
{
    /// <summary>Runs one queued import, if there is one. Returns false when the queue was empty.</summary>
    Task<bool> RunNextAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a specific import. Used by the worker and by the tests.</summary>
    Task RunAsync(HistoryImportDocument import, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class HistoryImportRunner(
    IHistoryImportStore store,
    IMessageAppService messageAppService,
    IMediaHelper mediaHelper,
    IObjectMessageSender objectMessageSender,
    IOptions<MyTelegramMessengerServerOptions> options,
    ILogger<HistoryImportRunner> logger)
    : IHistoryImportRunner, ITransientDependency
{
    /// <summary>A big export takes a while; the lease is renewed after every batch.</summary>
    private const int LeaseSeconds = 600;

    public async Task<bool> RunNextAsync(CancellationToken cancellationToken = default)
    {
        var import = await store.ClaimQueuedAsync(LeaseSeconds, cancellationToken);
        if (import == null)
        {
            return false;
        }

        try
        {
            await RunAsync(import, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "History import {ImportId} failed", import.Id);
            await store.FailAsync(import.Id, ex.Message, options.Value.HistoryImport.MaxAttempts,
                CancellationToken.None);
        }

        return true;
    }

    public async Task RunAsync(HistoryImportDocument import, CancellationToken cancellationToken = default)
    {
        var config = options.Value.HistoryImport;
        var peer = new Peer(Enum.Parse<PeerType>(import.PeerType), import.PeerId);
        var senderUserId = ResolveSenderUserId(peer, import.UserId);
        var requestInfo = BuildRequestInfo(import, senderUserId);
        var imported = import.ImportedCount;
        var lastReportedProgress = -1;

        logger.LogInformation("History import {ImportId} started: {Count} messages into {PeerType} {PeerId}",
            import.Id, import.TotalMessages, peer.PeerType, peer.PeerId);

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await store.ReadMessagesAsync(import.Id, imported, config.BatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var media = await store.GetMediaAsync(import.Id,
                [.. batch.Where(p => p.FileName != null).Select(p => p.FileName!)], cancellationToken);

            var inputs = batch.Select(p => BuildSendInput(requestInfo, senderUserId, peer, p, media)).ToList();
            await messageAppService.SendMessageAsync(inputs);

            imported = batch[^1].Seq + 1;
            await store.SetProgressAsync(import.Id, imported, cancellationToken);

            var progress = import.TotalMessages == 0
                ? 100
                : (int)Math.Min(100, 100L * imported / import.TotalMessages);
            if (progress != lastReportedProgress)
            {
                lastReportedProgress = progress;
                await PushProgressAsync(peer, import.UserId, progress);
            }

            if (config.BatchDelayMilliseconds > 0)
            {
                // The send pipeline is asynchronous; without a pause a large export would queue tens of
                // thousands of commands at once.
                await Task.Delay(config.BatchDelayMilliseconds, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        await store.SetStatusAsync(import.Id, HistoryImportStatus.Completed, cancellationToken);
        await store.CleanupAsync(import.Id, cancellationToken);

        logger.LogInformation("History import {ImportId} completed, {Count} messages imported", import.Id,
            imported);
    }

    /// <summary>
    /// Builds the message the way an imported message has to look: the text and the media are the ones
    /// of the export, the sender is the importing user, and the original author and date live in
    /// <c>fwd_from</c> with the <c>imported</c> flag set.
    /// </summary>
    private SendMessageInput BuildSendInput(RequestInfo requestInfo, long senderUserId, Peer peer,
        HistoryImportMessageDocument message, IReadOnlyDictionary<string, IMessageMedia> media)
    {
        IMessageMedia? attachment = null;
        if (message.FileName != null && media.TryGetValue(message.FileName, out var found))
        {
            attachment = found;
        }

        var text = message.Text;
        if (text.Length == 0 && attachment == null && message.FileName != null)
        {
            // The line refers to a file the client never uploaded: keep the reference rather than
            // importing an empty message.
            text = message.FileName;
        }

        var fwdHeader = new MessageFwdHeader
        {
            Imported = true,
            FromName = message.FromName,
            Date = message.Date
        };

        return new SendMessageInput(requestInfo,
            senderUserId,
            peer,
            text,
            Random.Shared.NextInt64(),
            media: attachment,
            // A plain text message is pushed as a short update, which has no room for fwd_from; the
            // forward subtype is what makes the client receive the full message with its header.
            sendMessageType: attachment == null ? SendMessageType.Text : SendMessageType.Media,
            messageType: mediaHelper.GeMessageType(attachment),
            fwdHeader: fwdHeader,
            messageSubType: MessageSubType.ForwardMessage,
            // Importing a year of history must not produce a year of notifications.
            silent: true,
            // The message carries the date it had in the chat it came from, the same one the forward
            // header reports; a client shows the plain "imported" marker when the two agree.
            date: message.Date);
    }

    /// <summary>
    /// Tells the chat how far the import has come, which is what the clients render as the
    /// "importing messages" progress bar.
    /// </summary>
    private async Task PushProgressAsync(Peer peer, long userId, int progress)
    {
        IUpdate update = peer.PeerType switch
        {
            PeerType.Channel => new TUpdateChannelUserTyping
            {
                ChannelId = peer.PeerId,
                FromId = new TPeerUser { UserId = userId },
                Action = new TSendMessageHistoryImportAction { Progress = progress }
            },
            _ => new TUpdateUserTyping
            {
                UserId = userId,
                Action = new TSendMessageHistoryImportAction { Progress = progress }
            }
        };

        await objectMessageSender.PushMessageToPeerAsync(peer, new TUpdateShort
        {
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Update = update
        });
    }

    /// <summary>
    /// Who the imported messages are sent by. In a group that is the chat importer account, exactly as
    /// on the official server: the original author is only a name in <c>fwd_from.from_name</c>, and
    /// attributing a whole imported history to the importing user would turn it into their own
    /// outgoing messages, and make forwarding one of them point back at them.
    /// A private chat has no third party, so there the importer stays the sender.
    /// See https://corefork.telegram.org/api/import
    /// </summary>
    private static long ResolveSenderUserId(Peer peer, long importerUserId)
    {
        return peer.PeerType is PeerType.Channel or PeerType.Chat
            ? MyTelegramConsts.ChatImporterBotUserId
            : importerUserId;
    }

    private static RequestInfo BuildRequestInfo(HistoryImportDocument import, long senderUserId)
    {
        // No rpc is waiting for these messages: they are produced by the worker, long after
        // startHistoryImport answered.
        return new RequestInfo(
            ConnectionId: "history-import",
            SessionId: 0,
            ReqMsgId: 0,
            UserId: senderUserId,
            AccessHashKeyId: 0,
            AuthKeyId: 0,
            PermAuthKeyId: 0,
            RequestId: Guid.NewGuid(),
            Layer: import.Layer,
            Date: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
