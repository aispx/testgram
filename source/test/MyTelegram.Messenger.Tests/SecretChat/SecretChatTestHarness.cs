using EventFlow;
using EventFlow.Aggregates;
using EventFlow.Aggregates.ExecutionResults;
using EventFlow.Commands;
using EventFlow.Core;
using EventFlow.Queries;
using Moq;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Abstractions;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram.Messenger.Services.Caching;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Messenger.Tests.SecretChat;

/// <summary>
/// Feature: secret-chats — shared hand-written fakes and builders for the secret-chat property tests.
/// Nothing here mocks the production logic under test: the real <see cref="SecretChatAppService"/>,
/// <see cref="SecretChatAccessResolver"/> and the real TL converters are exercised; only their
/// collaborators (query processor, command bus, id generator, block cache, update transport, stores)
/// are substituted so the tests stay deterministic and MongoDB-free where possible.
/// </summary>
internal static class SecretChatTestHarness
{
    public const long AdminId = 1001;
    public const long ParticipantId = 2002;
    public const long AdminPermAuthKeyId = 111;
    public const long ParticipantPermAuthKeyId = 222;
    public const int ChatId = 5;
    public const long AccessHash = 987654321;

    /// <summary>
    /// A structurally valid opaque send payload: exactly
    /// <see cref="SecretChatConsts.MinEncryptedPayloadLength"/> bytes, the floor the server enforces for
    /// key_fingerprint (8) + msg_key (16) + one AES block (16). The leading <paramref name="tag"/> bytes
    /// keep payloads from different sends distinguishable; the rest is deterministic filler. The server
    /// is a blind relay, so only the length is ever inspected.
    /// </summary>
    public static byte[] Payload(params byte[] tag)
    {
        var payload = new byte[SecretChatConsts.MinEncryptedPayloadLength];
        tag.CopyTo(payload, 0);
        for (var i = tag.Length; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31 + tag.Length) % 256);
        }

        return payload;
    }

    public static byte[] ValidDhValue()
    {
        // 256 bytes with the high byte set: satisfies 2^(2048-64) <= g <= p - 2^(2048-64).
        var value = new byte[256];
        value[0] = 0x7f;
        for (var i = 1; i < value.Length; i++)
        {
            value[i] = (byte)(i % 251 + 1);
        }

        return value;
    }

    public static FakeEncryptedChatReadModel Chat(ChatState state = ChatState.Active,
        long participantPermAuthKeyId = ParticipantPermAuthKeyId)
    {
        return new FakeEncryptedChatReadModel
        {
            Id = $"encrypted_chat_{ChatId}",
            ChatId = ChatId,
            AccessHash = AccessHash,
            AdminId = AdminId,
            ParticipantId = ParticipantId,
            AdminPermAuthKeyId = AdminPermAuthKeyId,
            ParticipantPermAuthKeyId = state == ChatState.Waiting ? 0 : participantPermAuthKeyId,
            Ga = ValidDhValue(),
            Gb = state == ChatState.Waiting ? [] : ValidDhValue(),
            KeyFingerprint = 424242,
            ChatState = state,
            Date = 1000,
            RandomId = 42
        };
    }

    public static IInputEncryptedChat InputChat(long accessHash = AccessHash, int chatId = ChatId)
    {
        return new TInputEncryptedChat { ChatId = chatId, AccessHash = accessHash };
    }

    public static TestRequestInput Input(long userId, long permAuthKeyId, int layer = 224)
    {
        return new TestRequestInput(userId, permAuthKeyId, layer);
    }

    public static ILayeredService<IEncryptedChatConverter> ChatConverters()
    {
        return new LayeredService<IEncryptedChatConverter>([new EncryptedChatConverter()]);
    }

    public static ILayeredService<IEncryptedMessageConverter> MessageConverters()
    {
        return new LayeredService<IEncryptedMessageConverter>([new EncryptedMessageConverter()]);
    }

    public static ILayeredService<IEncryptedFileConverter> FileConverters()
    {
        return new LayeredService<IEncryptedFileConverter>([new EncryptedFileConverter()]);
    }
}

internal sealed class FakeEncryptedChatReadModel : IEncryptedChatReadModel
{
    public long AccessHash { get; set; }
    public long AdminPermAuthKeyId { get; set; }
    public long AdminId { get; set; }
    public long ChatId { get; set; }
    public ChatState ChatState { get; set; }
    public int Date { get; set; }
    public byte[] Ga { get; set; } = [];
    public byte[] Gb { get; set; } = [];
    public bool HistoryDeleted { get; set; }
    public string Id { get; set; } = string.Empty;
    public long KeyFingerprint { get; set; }
    public long ParticipantPermAuthKeyId { get; set; }
    public long ParticipantId { get; set; }
    public long RandomId { get; set; }
    public List<long> SpamReporters { get; set; } = [];
}

internal static class FakeUser
{
    /// <summary>
    /// IUserReadModel has a very large surface; the secret-chat paths read only UserId, Bot and
    /// IsDeleted, so a loose Moq stub is the least noisy way to supply one.
    /// </summary>
    public static IUserReadModel Create(long userId, bool bot = false, bool? isDeleted = null)
    {
        var mock = new Mock<IUserReadModel>(MockBehavior.Loose);
        mock.SetupGet(u => u.UserId).Returns(userId);
        mock.SetupGet(u => u.Bot).Returns(bot);
        mock.SetupGet(u => u.IsDeleted).Returns(isDeleted);

        return mock.Object;
    }
}

/// <summary>
/// Resolves the secret-chat queries the production code issues: <see cref="GetEncryptedChatByIdQuery"/>
/// and <see cref="GetUserByIdQuery"/>. Unknown query types throw so a test never silently passes
/// against an unmodelled dependency.
/// </summary>
internal sealed class FakeQueryProcessor : IQueryProcessor
{
    public Dictionary<long, IEncryptedChatReadModel> Chats { get; } = new();
    public Dictionary<long, IUserReadModel> Users { get; } = new();
    public List<string> ExecutedQueries { get; } = [];

    public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        ExecutedQueries.Add(query.GetType().Name);

        switch (query)
        {
            case GetEncryptedChatByIdQuery chatQuery:
                Chats.TryGetValue(chatQuery.ChatId, out var chat);
                return Task.FromResult((TResult)(object)chat!);
            case GetUserByIdQuery userQuery:
                Users.TryGetValue(userQuery.UserId, out var user);
                return Task.FromResult((TResult)(object)user!);
            default:
                throw new NotSupportedException($"Unexpected query type {query.GetType().Name}");
        }
    }
}

internal sealed class RecordingCommandBus : ICommandBus
{
    public List<ICommand> Published { get; } = [];

    /// <summary>When set, PublishAsync throws — used by the failure-atomicity property.</summary>
    public Exception? ThrowOnPublish { get; set; }

    public Task<TExecutionResult> PublishAsync<TAggregate, TIdentity, TExecutionResult>(
        ICommand<TAggregate, TIdentity, TExecutionResult> command,
        CancellationToken cancellationToken)
        where TAggregate : IAggregateRoot<TIdentity>
        where TIdentity : IIdentity
        where TExecutionResult : IExecutionResult
    {
        if (ThrowOnPublish != null)
        {
            throw ThrowOnPublish;
        }

        Published.Add(command);

        return Task.FromResult((TExecutionResult)(object)ExecutionResult.Success());
    }
}

internal sealed class FakeIdGenerator : IIdGenerator
{
    private int _nextId;
    private long _nextLongId;

    public FakeIdGenerator(int firstId = 5)
    {
        _nextId = firstId;
        _nextLongId = firstId;
    }

    public Task<int> NextIdAsync(IdType idType, long id, int step = 1, CancellationToken cancellationToken = default)
    {
        var value = _nextId;
        _nextId += step;

        return Task.FromResult(value);
    }

    public Task<long> NextLongIdAsync(IdType idType, long id = 0, int step = 1,
        CancellationToken cancellationToken = default)
    {
        var value = _nextLongId;
        _nextLongId += step;

        return Task.FromResult(value);
    }
}

internal sealed class FakeBlockCacheAppService : IBlockCacheAppService
{
    public HashSet<(long BlockerId, long BlockedId)> Blocks { get; } = [];

    public Task<bool> IsBlockedAsync(long userId, long targetPeerId)
    {
        return Task.FromResult(Blocks.Contains((userId, targetPeerId)));
    }

    public Task BlockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User,
        bool myStoriesFrom = false) => Task.CompletedTask;

    public Task<BlockedPeerCachePage> GetBlockedAsync(long userId, int offset, int limit,
        bool myStoriesFrom = false) => Task.FromResult(new BlockedPeerCachePage(0, []));

    public Task UnblockAsync(long userId, long targetPeerId, PeerType targetPeerType = PeerType.User,
        bool myStoriesFrom = false) => Task.CompletedTask;

    public Task ReplaceBlockedAsync(long userId, IReadOnlyCollection<Peer> peers, bool myStoriesFrom = false)
        => Task.CompletedTask;
}

/// <summary>A dispatched secret-chat update, captured for fan-out assertions.</summary>
internal sealed record DispatchedUpdate(
    long UserId,
    IUpdate Update,
    long? OnlySendToThisAuthKeyId,
    long? ExcludeAuthKeyId,
    int? Qts,
    PushData? PushData);

internal sealed class RecordingUpdateDispatcher : ISecretChatUpdateDispatcher
{
    public List<DispatchedUpdate> Dispatched { get; } = [];

    public Task PushToAllDevicesAsync(long userId, IUpdate update, long? excludeAuthKeyId = null,
        PushData? pushData = null)
    {
        Dispatched.Add(new DispatchedUpdate(userId, update, null, excludeAuthKeyId, null, pushData));

        return Task.CompletedTask;
    }

    public Task PushToDeviceAsync(long userId, long permAuthKeyId, IUpdate update, int? qts = null,
        PushData? pushData = null)
    {
        Dispatched.Add(new DispatchedUpdate(userId, update, permAuthKeyId, null, qts, pushData));

        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ISecretChatMessageStore"/> with the same semantics as the Mongo store.</summary>
internal sealed class InMemorySecretChatMessageStore : ISecretChatMessageStore
{
    private readonly Dictionary<string, EncryptedMessageDocument> _messages = new();
    private readonly Dictionary<string, int> _counters = new();
    private readonly Dictionary<string, int> _delivered = new();

    /// <summary>Allocated-but-not-yet-committed qts per device, mirroring the Mongo "Inflight" array.</summary>
    private readonly Dictionary<string, HashSet<int>> _inflight = new();

    public Exception? ThrowOnStore { get; set; }

    public IReadOnlyCollection<EncryptedMessageDocument> All => _messages.Values;

    public Task<EncryptedMessageDocument?> FindAsync(long chatId, long senderUserId, long randomId)
    {
        _messages.TryGetValue(EncryptedMessageDocument.BuildId(chatId, senderUserId, randomId), out var doc);

        return Task.FromResult(doc);
    }

    public Task<EncryptedMessageStoreResult> StoreAsync(EncryptedMessageDocument document)
    {
        if (ThrowOnStore != null)
        {
            throw ThrowOnStore;
        }

        if (_messages.TryGetValue(document.Id, out var existing))
        {
            return Task.FromResult(new EncryptedMessageStoreResult(false, existing));
        }

        _messages[document.Id] = document;

        return Task.FromResult(new EncryptedMessageStoreResult(true, document));
    }

    public Task<int> AllocateQtsAsync(long userId, long permAuthKeyId)
    {
        var key = $"{userId}_{permAuthKeyId}";
        _counters.TryGetValue(key, out var seq);
        seq++;
        _counters[key] = seq;

        var qts = SecretChatConsts.QtsInitialValue - 1 + seq;

        // Registered atomically with the increment, exactly as the production pipeline update does.
        if (!_inflight.TryGetValue(key, out var live))
        {
            live = [];
            _inflight[key] = live;
        }

        live.Add(qts);

        return Task.FromResult(qts);
    }

    public Task<bool> SetQtsAsync(string id, int qts, long recipientUserId, long recipientPermAuthKeyId)
    {
        var key = $"{recipientUserId}_{recipientPermAuthKeyId}";

        // Conditional on Qts == 0: a row another request already sequenced must not be pushed twice.
        if (_messages.TryGetValue(id, out var doc))
        {
            if (doc.Qts != 0)
            {
                _inflight.GetValueOrDefault(key)?.Remove(qts);

                return Task.FromResult(false);
            }

            doc.Qts = qts;
        }

        // Mirrors the production store: the delivered watermark advances and the allocation is released
        // only once the row is visible.
        _delivered.TryGetValue(key, out var current);
        _delivered[key] = Math.Max(current, qts);
        _inflight.GetValueOrDefault(key)?.Remove(qts);

        return Task.FromResult(true);
    }

    public Task AbandonQtsAsync(int qts, long recipientUserId, long recipientPermAuthKeyId)
    {
        _inflight.GetValueOrDefault($"{recipientUserId}_{recipientPermAuthKeyId}")?.Remove(qts);

        return Task.CompletedTask;
    }

    public Task<int> GetHighestQtsAsync(long userId, long permAuthKeyId)
    {
        var key = $"{userId}_{permAuthKeyId}";
        _delivered.TryGetValue(key, out var delivered);
        if (delivered == 0)
        {
            delivered = SecretChatConsts.QtsInitialValue - 1;
        }

        // Clamped below the lowest live allocation: the delivered watermark is a max, so on its own it
        // would carry over an earlier allocation whose row is still unwritten. Staleness expiry is not
        // modelled here — no in-memory test holds an allocation for a minute.
        var live = _inflight.GetValueOrDefault(key);

        return Task.FromResult(live is { Count: > 0 } ? Math.Min(delivered, live.Min() - 1) : delivered);
    }

    public Task<int> GetAssignedQtsAsync(long userId, long permAuthKeyId)
    {
        _counters.TryGetValue($"{userId}_{permAuthKeyId}", out var seq);

        return Task.FromResult(SecretChatConsts.QtsInitialValue - 1 + seq);
    }

    public Task<IReadOnlyList<long>> AckAsync(long userId, long permAuthKeyId, int maxQts)
    {
        var acked = new List<long>();
        foreach (var doc in _messages.Values
                     .Where(d => d.RecipientUserId == userId
                                 && d.RecipientPermAuthKeyId == permAuthKeyId
                                 && !d.Acked
                                 && d.Qts > 0
                                 && d.Qts <= maxQts)
                     .OrderBy(d => d.Qts)
                     .ToList())
        {
            doc.Acked = true;
            acked.Add(doc.RandomId);
        }

        return Task.FromResult<IReadOnlyList<long>>(acked);
    }

    public Task<IReadOnlyList<EncryptedMessageDocument>> GetForDifferenceAsync(long userId, long permAuthKeyId,
        int sinceQts, int limit, int maxQts = int.MaxValue)
    {
        var result = _messages.Values
            .Where(d => d.RecipientUserId == userId
                        && d.RecipientPermAuthKeyId == permAuthKeyId
                        && !d.Acked
                        && d.Qts > sinceQts
                        && d.Qts <= maxQts)
            .OrderBy(d => d.Qts)
            .Take(limit > 0 ? limit : int.MaxValue)
            .ToList();

        return Task.FromResult<IReadOnlyList<EncryptedMessageDocument>>(result);
    }

    public Task DeleteByChatAsync(long chatId)
    {
        foreach (var key in _messages.Where(kv => kv.Value.ChatId == chatId).Select(kv => kv.Key).ToList())
        {
            _messages.Remove(key);
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemorySecretChatRequestLedger : ISecretChatRequestLedger
{
    private readonly Dictionary<string, SecretChatRequestDocument> _rows = new();

    public Task<SecretChatRequestDocument?> FindAsync(long adminId, int randomId)
    {
        _rows.TryGetValue(SecretChatRequestDocument.BuildId(adminId, randomId), out var row);

        return Task.FromResult(row);
    }

    public Task<SecretChatRequestDocument> ReserveAsync(SecretChatRequestDocument document)
    {
        if (_rows.TryGetValue(document.Id, out var existing))
        {
            return Task.FromResult(existing);
        }

        _rows[document.Id] = document;

        return Task.FromResult(document);
    }
}

internal sealed class InMemoryEncryptedFileStore : IEncryptedFileStore
{
    private readonly Dictionary<(long Id, long AccessHash), EncryptedFileDescriptor> _files = new();
    private long _nextId = 1;

    public int StoreUploadedCallCount { get; private set; }
    public int ResolveCallCount { get; private set; }

    /// <summary>Parts registered per (userId, clientFileId), mirroring the file_parts collection.</summary>
    public Dictionary<(long UserId, long FileId), byte[][]> Parts { get; } = new();

    public Task<EncryptedFileDescriptor> StoreUploadedAsync(long userId, long clientFileId, int declaredParts,
        int keyFingerprint, string? md5Checksum)
    {
        StoreUploadedCallCount++;

        if (!Parts.TryGetValue((userId, clientFileId), out var parts) || parts.Length == 0)
        {
            RpcErrors.RpcErrors400.FileEmtpy.ThrowRpcError();
        }

        if (declaredParts > 0 && parts!.Length != declaredParts)
        {
            RpcErrors.RpcErrors400.FilePartsInvalid.ThrowRpcError();
        }

        var id = _nextId++;
        var descriptor = new EncryptedFileDescriptor(id, id * 31 + 7, parts!.Sum(p => p.LongLength), 1,
            keyFingerprint);
        _files[(descriptor.Id, descriptor.AccessHash)] = descriptor;

        return Task.FromResult(descriptor);
    }

    public Task<EncryptedFileDescriptor?> ResolveAsync(long fileId, long accessHash)
    {
        ResolveCallCount++;
        _files.TryGetValue((fileId, accessHash), out var descriptor);

        return Task.FromResult(descriptor);
    }

    public Task<(EncryptedFileDocument Document, byte[] Blob)?> LoadForDownloadAsync(long fileId, long accessHash)
    {
        return Task.FromResult<(EncryptedFileDocument, byte[])?>(null);
    }

    /// <summary>
    /// The in-memory store keeps descriptors only (no blobs), so the download path is not modelled here —
    /// the ranged read is covered against the real Mongo-backed store.
    /// </summary>
    public Task<(EncryptedFileDocument Document, byte[] Bytes)?> LoadRangeAsync(long fileId,
        long accessHash,
        long offset,
        int limit)
    {
        return Task.FromResult<(EncryptedFileDocument, byte[])?>(null);
    }
}

internal sealed class TestRequestInput(long userId, long permAuthKeyId, int layer) : IRequestInput
{
    public long UserId { get; } = userId;
    public int Layer { get; set; } = layer;
    public string ConnectionId => string.Empty;
    public ConnectionType ConnectionType => default;
    public long AuthKeyId => 0;
    public uint ObjectId { get; set; }
    public long PermAuthKeyId { get; } = permAuthKeyId;
    public long ReqMsgId => 0;
    public int SeqNumber => 0;
    public Guid RequestId => Guid.Empty;
    public long Date => 0;
    public DeviceType DeviceType { get; set; }
    public string ClientIp => string.Empty;
    public long SessionId => 0;
    public long AccessHashKeyId { get; set; }
}
