using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.HistoryImport;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.HistoryImport;

/// <summary>
/// Feature: imported messages.
///
/// <para>
/// "messageFwdHeader#4e4df4bb flags:# imported:flags.7?true ... from_name:flags.5?string date:int":
/// an imported message is sent by the importing user, but carries the original author and the original
/// date in its forward header, which is what every client keys its imported-message rendering off.
/// The chat is told how far the import has come through <c>sendMessageHistoryImportAction</c>.
/// See https://corefork.telegram.org/api/import
/// </para>
/// </summary>
public class HistoryImportRunnerTests
{
    private const long UserId = 2010001;
    private const long ChannelId = 1000001;

    [Fact]
    public async Task An_imported_message_carries_the_original_author_and_date()
    {
        var (runner, sent, _) = CreateRunner(Messages(("John Doe", 1609459140, "Happy new year!", null)));

        await runner.RunAsync(Import(1));

        var input = sent.Single();
        input.Message.ShouldBe("Happy new year!");

        // The message itself is dated as it was in the chat it came from, so the history keeps its own
        // day separators instead of arriving under "today"; the client shows the plain "imported"
        // marker exactly because the message date and the forward date agree.
        input.Date.ShouldBe(1609459140);
        input.FwdHeader.ShouldNotBeNull();
        input.FwdHeader!.Imported.ShouldBeTrue();
        input.FwdHeader.FromName.ShouldBe("John Doe");
        input.FwdHeader.Date.ShouldBe(1609459140);
        input.FwdHeader.FromId.ShouldBeNull();
    }

    [Fact]
    public async Task A_group_history_is_imported_by_the_chat_importer_account()
    {
        var (runner, sent, _) = CreateRunner(Messages(("John Doe", 1609459140, "hi", null)));

        await runner.RunAsync(Import(1));

        // Attributing the whole history to the importing user would turn it into their own outgoing
        // messages, and forwarding one of them would point back at them.
        sent.Single().SenderUserId.ShouldBe(MyTelegramConsts.ChatImporterBotUserId);
        sent.Single().RequestInfo.UserId.ShouldBe(MyTelegramConsts.ChatImporterBotUserId);
    }

    [Fact]
    public async Task A_private_chat_history_is_imported_by_the_user_who_started_it()
    {
        var (runner, sent, _) = CreateRunner(Messages(("John Doe", 1609459140, "hi", null)));

        var import = Import(1);
        import.PeerType = PeerType.User.ToString();
        import.PeerId = 2010002;

        await runner.RunAsync(import);

        // A private chat has no third party that could hold the messages.
        sent.Single().SenderUserId.ShouldBe(UserId);
    }

    [Fact]
    public async Task An_imported_message_is_sent_as_a_forward_so_the_header_reaches_the_client()
    {
        var (runner, sent, _) = CreateRunner(Messages(("John", 1609459140, "hi", null)));

        await runner.RunAsync(Import(1));

        // A plain text message goes out as a short update, which has no room for fwd_from.
        sent.Single().MessageSubType.ShouldBe(MessageSubType.ForwardMessage);
        // A year of history must not produce a year of notifications.
        sent.Single().Silent.ShouldBeTrue();
    }

    [Fact]
    public async Task Uploaded_media_is_attached_to_the_line_that_names_it()
    {
        var (runner, sent, _) = CreateRunner(
            Messages(("John", 1609459140, string.Empty, "IMG-0001.jpg"), ("Jane", 1609459141, "hi", null)),
            media: new Dictionary<string, IMessageMedia>
            {
                ["IMG-0001.jpg"] = new TMessageMediaEmpty()
            });

        await runner.RunAsync(Import(2));

        sent[0].Media.ShouldBeOfType<TMessageMediaEmpty>();
        sent[0].SendMessageType.ShouldBe(SendMessageType.Media);
        sent[1].Media.ShouldBeNull();
        sent[1].SendMessageType.ShouldBe(SendMessageType.Text);
    }

    [Fact]
    public async Task A_media_line_whose_file_never_arrived_keeps_the_file_name_as_its_text()
    {
        var (runner, sent, _) = CreateRunner(Messages(("John", 1609459140, string.Empty, "IMG-0001.jpg")));

        await runner.RunAsync(Import(1));

        // Losing the line entirely would be worse than importing it as a reference.
        sent.Single().Media.ShouldBeNull();
        sent.Single().Message.ShouldBe("IMG-0001.jpg");
    }

    [Fact]
    public async Task The_chat_is_told_how_far_the_import_has_come()
    {
        var messages = Enumerable.Range(0, 4)
            .Select(i => ($"John", 1609459140 + i, $"m{i}", (string?)null)).ToArray();
        var (runner, _, sender) = CreateRunner(Messages(messages), batchSize: 2);

        await runner.RunAsync(Import(4));

        // Two batches of two: 50% and then 100%.
        sender.Progress.ShouldBe([50, 100]);
    }

    [Fact]
    public async Task A_completed_import_is_marked_and_cleaned_up()
    {
        var store = new FakeStore(Messages(("John", 1609459140, "hi", null)), []);
        var runner = CreateRunner(store, out _, out _);

        await runner.RunAsync(Import(1));

        store.Status.ShouldBe(HistoryImportStatus.Completed);
        store.CleanedUp.ShouldBeTrue();
        store.Progress.ShouldBe(1);
    }

    private static HistoryImportDocument Import(int totalMessages)
    {
        return new HistoryImportDocument
        {
            Id = 7,
            UserId = UserId,
            PeerId = ChannelId,
            PeerType = PeerType.Channel.ToString(),
            Format = ChatExportFormat.WhatsApp.ToString(),
            TotalMessages = totalMessages,
            Status = HistoryImportStatus.Running,
            Layer = 222
        };
    }

    private static List<HistoryImportMessageDocument> Messages(
        params (string FromName, int Date, string Text, string? FileName)[] lines)
    {
        return [.. lines.Select((line, index) => new HistoryImportMessageDocument
        {
            Id = $"7_{index}",
            ImportId = 7,
            Seq = index,
            Date = line.Date,
            FromName = line.FromName,
            Text = line.Text,
            FileName = line.FileName
        })];
    }

    private static (HistoryImportRunner Runner, List<SendMessageInput> Sent, RecordingProgressSender Sender)
        CreateRunner(List<HistoryImportMessageDocument> messages,
            Dictionary<string, IMessageMedia>? media = null, int batchSize = 50)
    {
        var store = new FakeStore(messages, media ?? []);
        var runner = CreateRunner(store, out var sent, out var sender, batchSize);

        return (runner, sent, sender);
    }

    private static HistoryImportRunner CreateRunner(FakeStore store, out List<SendMessageInput> sent,
        out RecordingProgressSender sender, int batchSize = 50)
    {
        var captured = new List<SendMessageInput>();
        sent = captured;

        var messageAppService = new Mock<IMessageAppService>(MockBehavior.Loose);
        messageAppService.Setup(p => p.SendMessageAsync(It.IsAny<List<SendMessageInput>>()))
            .Callback<List<SendMessageInput>>(captured.AddRange)
            .Returns(Task.CompletedTask);

        var mediaHelper = new Mock<IMediaHelper>(MockBehavior.Loose);
        mediaHelper.Setup(p => p.GeMessageType(It.IsAny<IMessageMedia?>())).Returns(MessageType.Text);

        var progressSender = new RecordingProgressSender();
        sender = progressSender;

        var options = Microsoft.Extensions.Options.Options.Create(new MyTelegramMessengerServerOptions
        {
            HistoryImport = new HistoryImportConfig { BatchSize = batchSize, BatchDelayMilliseconds = 0 }
        });

        return new HistoryImportRunner(store, messageAppService.Object, mediaHelper.Object, progressSender,
            options, NullLogger<HistoryImportRunner>.Instance);
    }

    /// <summary>Records the progress reported through <c>sendMessageHistoryImportAction</c>.</summary>
    private sealed class RecordingProgressSender : IObjectMessageSender
    {
        public List<int> Progress { get; } = [];

        public Task PushMessageToPeerAsync<TData>(Peer peer, TData data, long? excludeAuthKeyId = null,
            long? excludeUserId = null, long? onlySendToUserId = null, long? onlySendToThisAuthKeyId = null,
            int pts = 0, int? qts = null, long globalSeqNo = 0, PushData? pushData = null,
            List<long>? excludeUserIds = null) where TData : IObject
        {
            if (data is TUpdateShort { Update: TUpdateChannelUserTyping typing } &&
                typing.Action is TSendMessageHistoryImportAction action)
            {
                Progress.Add(action.Progress);
            }

            return Task.CompletedTask;
        }

        public Task PushSessionMessageToAuthKeyIdAsync<TData>(long authKeyId, TData data, int pts = 0,
            int? qts = null, long globalSeqNo = 0) where TData : IObject => Task.CompletedTask;

        public Task SendFileDataToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
            Task.CompletedTask;

        public Task SendMessageToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject =>
            Task.CompletedTask;

        public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, int pts = 0)
            where TData : IObject => Task.CompletedTask;

        public Task SendRpcMessageToClientAsync<TData>(string connectionId, long tempAuthKeyId, long sessionId,
            long reqMsgId, TData data, int pts = 0, long permAuthKeyId = 0) where TData : IObject =>
            Task.CompletedTask;

        public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, long authKeyId,
            long permAuthKeyId, long userId, int pts = 0) where TData : IObject => Task.CompletedTask;
    }

    /// <summary>In memory stand in for the MongoDB backed store.</summary>
    private sealed class FakeStore(List<HistoryImportMessageDocument> messages,
        Dictionary<string, IMessageMedia> media) : IHistoryImportStore
    {
        public HistoryImportStatus Status { get; private set; } = HistoryImportStatus.Running;
        public bool CleanedUp { get; private set; }
        public int Progress { get; private set; }

        public Task EnsureIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<HistoryImportDocument?> GetAsync(long importId, CancellationToken cancellationToken = default)
            => Task.FromResult<HistoryImportDocument?>(null);

        public Task<HistoryImportDocument?> GetUnfinishedForPeerAsync(Peer peer,
            CancellationToken cancellationToken = default) => Task.FromResult<HistoryImportDocument?>(null);

        public Task<HistoryImportDocument> CreateAsync(long userId, Peer peer, ChatExportFormat format,
            int mediaCount, int layer, IReadOnlyList<ImportedMessageLine> lines,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<List<HistoryImportMessageDocument>> ReadMessagesAsync(long importId, int fromSeq, int take,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(messages.Where(p => p.Seq >= fromSeq).OrderBy(p => p.Seq).Take(take).ToList());
        }

        public Task SaveMediaAsync(long importId, string fileName, IMessageMedia value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Dictionary<string, IMessageMedia>> GetMediaAsync(long importId,
            IReadOnlyCollection<string> fileNames, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(media
                .Where(p => fileNames.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        }

        public Task SetStatusAsync(long importId, HistoryImportStatus status,
            CancellationToken cancellationToken = default)
        {
            Status = status;
            return Task.CompletedTask;
        }

        public Task SetProgressAsync(long importId, int importedCount,
            CancellationToken cancellationToken = default)
        {
            Progress = importedCount;
            return Task.CompletedTask;
        }

        public Task<HistoryImportDocument?> ClaimQueuedAsync(int leaseSeconds,
            CancellationToken cancellationToken = default) => Task.FromResult<HistoryImportDocument?>(null);

        public Task FailAsync(long importId, string error, int maxAttempts,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CleanupAsync(long importId, CancellationToken cancellationToken = default)
        {
            CleanedUp = true;
            return Task.CompletedTask;
        }
    }
}
