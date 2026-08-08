using MongoDB.Driver;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyTelegram.Messenger.Services.Phone;

internal static class GroupCallStateHelper
{
    private const int DefaultGroupCallStreamDcId = 1;

    public static FilterDefinition<GroupCallDocument> Filter(TInputGroupCall call)
    {
        // Call ids are sequential and therefore guessable, so the access hash is the capability
        // token that proves the client legitimately learned of this call. Match on both.
        return Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, call.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, call.AccessHash));
    }

    public static FilterDefinition<GroupCallDocument> Filter(IInputGroupCall call, long userId)
    {
        return call switch
        {
            TInputGroupCall inputGroupCall => Filter(inputGroupCall),
            TInputGroupCallSlug slug => Builders<GroupCallDocument>.Filter.Or(
                Builders<GroupCallDocument>.Filter.Eq(g => g.InviteHash, slug.Slug),
                Builders<GroupCallDocument>.Filter.Eq(g => g.InviteLink, TelegramDeepLinkHelper.GetConferenceCallLink(slug.Slug))),
            TInputGroupCallInviteMessage inviteMessage => Builders<GroupCallDocument>.Filter.ElemMatch(
                g => g.InviteMessages,
                invite => invite.UserId == userId && invite.MessageId == inviteMessage.MsgId),
            _ => Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, long.MinValue)
        };
    }

    public static async Task EnsureCanManageCallAsync(
        GroupCallDocument call,
        long userId,
        IChannelAdminRightsChecker channelAdminRightsChecker,
        RpcError? forbiddenError = null)
    {
        RpcError error = forbiddenError ?? RpcErrors.RpcErrors400.ChatAdminRequired;
        if (call.CreatorId == userId)
        {
            return;
        }

        if ((PeerType)call.PeerType == PeerType.Channel)
        {
            await channelAdminRightsChecker.CheckAdminRightAsync(
                call.PeerId,
                userId,
                rights => rights.ManageCall,
                error);
            return;
        }

        error.ThrowRpcError();
    }

    public static TInputGroupCall ToInputGroupCall(GroupCallDocument call)
    {
        return new TInputGroupCall
        {
            Id = call.CallId,
            AccessHash = call.AccessHash
        };
    }

    public static TGroupCall ToGroupCall(GroupCallDocument call, long selfUserId, long? accessHash = null)
    {
        return new TGroupCall
        {
            Id = call.CallId,
            AccessHash = accessHash ?? call.AccessHash,
            ParticipantsCount = call.Participants.Count(p => !p.Left),
            Title = call.Title,
            StreamDcId = DefaultGroupCallStreamDcId,
            ScheduleDate = call.ScheduleDate,
            RecordStartDate = call.RecordStartDate,
            RecordVideoActive = call.RecordVideoActive,
            Version = call.Version,
            JoinMuted = call.JoinMuted,
            ScheduleStartSubscribed = call.ScheduleStartSubscriberIds.Contains(selfUserId),
            RtmpStream = call.RtmpStream,
            Conference = call.Conference,
            Creator = call.CreatorId == selfUserId,
            MessagesEnabled = call.MessagesEnabled,
            CanChangeJoinMuted = true,
            CanChangeMessagesEnabled = true,
            CanStartVideo = true,
            UnmutedVideoCount = call.Participants.Count(p => !p.Left && !p.VideoStopped),
            UnmutedVideoLimit = 100,
            InviteLink = call.Conference ? call.InviteLink : null,
            SendPaidMessagesStars = call.SendPaidMessagesStars,
            DefaultSendAs = call.DefaultSendAsPeerId.HasValue && call.DefaultSendAsPeerType.HasValue
                ? ToPeer((PeerType)call.DefaultSendAsPeerType.Value, call.DefaultSendAsPeerId.Value)
                : null
        };
    }

    public static async Task<GroupCallDocument?> FindActivePeerCallAsync(
        IMongoDatabase database,
        long peerId,
        PeerType peerType)
    {
        var collection = database.GetCollection<GroupCallDocument>("group_calls");
        var filter = Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerId, peerId),
            Builders<GroupCallDocument>.Filter.Eq(call => call.PeerType, (int)peerType),
            Builders<GroupCallDocument>.Filter.Eq(call => call.Active, true),
            Builders<GroupCallDocument>.Filter.Eq(call => call.LiveStory, false));

        return await collection
            .Find(filter)
            .SortByDescending(call => call.Date)
            .FirstOrDefaultAsync();
    }

    public static void ApplyActiveCallToChatFull(
        MyTelegram.Schema.Messages.IChatFull chatFull,
        GroupCallDocument call,
        long selfUserId)
    {
        var inputCall = ToInputGroupCall(call);
        var defaultJoinAs = new TPeerUser { UserId = selfUserId };

        switch (chatFull.FullChat)
        {
            case TChannelFull channelFull:
                channelFull.Call = inputCall;
                channelFull.GroupcallDefaultJoinAs = defaultJoinAs;
                break;
            case MyTelegram.Schema.TChatFull basicChatFull:
                basicChatFull.Call = inputCall;
                basicChatFull.GroupcallDefaultJoinAs = defaultJoinAs;
                break;
        }

        var hasMedia = call.RtmpStream || call.Participants.Any(participant => !participant.Left);
        foreach (var chat in chatFull.Chats)
        {
            switch (chat)
            {
                case TChannel channel when call.PeerType == (int)PeerType.Channel && channel.Id == call.PeerId:
                    channel.CallActive = true;
                    channel.CallNotEmpty = hasMedia;
                    break;
                case TChat basicChat when call.PeerType == (int)PeerType.Chat && basicChat.Id == call.PeerId:
                    basicChat.CallActive = true;
                    basicChat.CallNotEmpty = hasMedia;
                    break;
            }
        }
    }

    public static async Task SendGroupCallServiceMessageAsync(
        IMessageAppService messageAppService,
        IRequestInput input,
        GroupCallDocument call,
        IMessageAction action)
    {
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            new Peer((PeerType)call.PeerType, call.PeerId),
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action);

        await messageAppService.SendMessageAsync([sendInput]);
    }

    public static TGroupCallDiscarded ToDiscardedGroupCall(GroupCallDocument call, int date)
    {
        return new TGroupCallDiscarded
        {
            Id = call.CallId,
            AccessHash = call.AccessHash,
            Duration = GetCallDuration(call, date)
        };
    }

    public static int GetCallDuration(GroupCallDocument call, int date)
    {
        return Math.Max(1, date - call.Date);
    }

    public static TGroupCallParticipant ToParticipant(
        GroupCallParticipantDoc participant,
        long selfUserId,
        IPeerHelper peerHelper,
        bool justJoined = false,
        GroupCallDocument? call = null)
    {
        return new TGroupCallParticipant
        {
            Peer = peerHelper.ToPeer((PeerType)participant.PeerType, participant.PeerId),
            Source = participant.Source,
            Date = participant.Date,
            ActiveDate = participant.Date,
            Muted = participant.Muted,
            CanSelfUnmute = true,
            JustJoined = justJoined,
            Self = call == null
                ? IsParticipantControlledByUser(participant, selfUserId)
                : IsParticipantControlledByUser(call, participant, selfUserId),
            Left = participant.Left,
            VideoJoined = !participant.Left && !participant.VideoStopped,
            Volume = participant.Volume,
            RaiseHandRating = participant.RaiseHand ? participant.Date : null,
            Versioned = true
        };
    }

    public static TUpdateGroupCall CreateCallUpdate(
        GroupCallDocument call,
        long selfUserId,
        IPeerHelper peerHelper)
    {
        return new TUpdateGroupCall
        {
            LiveStory = call.LiveStory,
            Peer = peerHelper.ToPeer((PeerType)call.PeerType, call.PeerId),
            Call = ToGroupCall(call, selfUserId)
        };
    }

    public static IUpdate CreatePeerChangedUpdate(GroupCallDocument call)
    {
        return (PeerType)call.PeerType switch
        {
            PeerType.Channel => new TUpdateChannel { ChannelId = call.PeerId },
            PeerType.Chat => new TUpdateChat { ChatId = call.PeerId },
            _ => new TUpdateUser { UserId = call.PeerId }
        };
    }

    public static TUpdateGroupCallParticipants CreateParticipantsUpdate(
        GroupCallDocument call,
        long selfUserId,
        IPeerHelper peerHelper,
        IEnumerable<GroupCallParticipantDoc> participants,
        bool justJoined = false)
    {
        return new TUpdateGroupCallParticipants
        {
            Call = ToInputGroupCall(call),
            Participants = new TVector<IGroupCallParticipant>(
                participants.Select(p => (IGroupCallParticipant)ToParticipant(p, selfUserId, peerHelper, justJoined, call))),
            Version = call.Version
        };
    }

    public static TUpdateGroupCallChainBlocks CreateChainBlocksUpdate(
        GroupCallDocument call,
        int subChainId,
        IEnumerable<byte[]> blocks,
        int nextOffset)
    {
        return new TUpdateGroupCallChainBlocks
        {
            Call = ToInputGroupCall(call),
            SubChainId = subChainId,
            Blocks = new TVector<ReadOnlyMemory<byte>>(blocks.Select(block => (ReadOnlyMemory<byte>)block).ToList()),
            NextOffset = nextOffset
        };
    }

    public static TUpdateGroupCallMessage CreateMessageUpdate(GroupCallDocument call, IGroupCallMessage message)
    {
        return new TUpdateGroupCallMessage
        {
            Call = ToInputGroupCall(call),
            Message = message
        };
    }

    public static TUpdateGroupCallEncryptedMessage CreateEncryptedMessageUpdate(
        GroupCallDocument call,
        IPeer fromId,
        ReadOnlyMemory<byte> encryptedMessage)
    {
        return new TUpdateGroupCallEncryptedMessage
        {
            Call = ToInputGroupCall(call),
            FromId = fromId,
            EncryptedMessage = encryptedMessage
        };
    }

    public static TUpdateDeleteGroupCallMessages CreateDeleteMessagesUpdate(
        GroupCallDocument call,
        IEnumerable<int> messageIds)
    {
        return new TUpdateDeleteGroupCallMessages
        {
            Call = ToInputGroupCall(call),
            Messages = new TVector<int>(messageIds)
        };
    }

    public static IReadOnlyList<byte[]> GetChainBlocksPage(
        GroupCallDocument call,
        int subChainId,
        int offset,
        int limit,
        out int nextOffset)
    {
        var blocks = call.ChainBlocks
            .Where(block => block.SubChainId == subChainId)
            .Select(block => block.Block)
            .ToList();

        var normalizedLimit = limit > 0 ? Math.Min(limit, 100) : 100;
        var normalizedOffset = offset < 0
            ? Math.Max(0, blocks.Count - normalizedLimit)
            : Math.Min(offset, blocks.Count);
        var page = blocks.Skip(normalizedOffset).Take(normalizedLimit).ToList();
        nextOffset = normalizedOffset + page.Count;
        return page;
    }

    public static IEnumerable<long> GetParticipantUserIds(GroupCallDocument call)
    {
        var participantUserIds = call.Participants
            .Where(participant => participant.PeerType == (int)PeerType.User && !participant.Left)
            .Select(participant => participant.PeerId);
        var joinedUserIds = call.Participants
            .Where(participant => participant.UserId > 0 && !participant.Left)
            .Select(participant => participant.UserId);

        return participantUserIds.Concat(joinedUserIds).Distinct();
    }

    public static bool IsJoinedByUser(GroupCallDocument call, long userId)
    {
        return FindParticipantByUser(call, userId) != null;
    }

    public static GroupCallParticipantDoc? FindParticipantByUser(GroupCallDocument call, long userId, int? source = null)
    {
        if (userId <= 0)
        {
            return null;
        }

        return call.Participants.FirstOrDefault(participant =>
            !participant.Left &&
            (!source.HasValue || participant.Source == source.Value) &&
            IsParticipantControlledByUser(call, participant, userId));
    }

    public static bool IsParticipantControlledByUser(
        GroupCallParticipantDoc participant,
        long userId)
    {
        return participant.UserId == userId ||
               (participant.PeerType == (int)PeerType.User && participant.PeerId == userId);
    }

    public static bool IsParticipantControlledByUser(
        GroupCallDocument call,
        GroupCallParticipantDoc participant,
        long userId)
    {
        return IsParticipantControlledByUser(participant, userId) ||
               (participant.UserId == 0 &&
                call.CreatorId == userId &&
                participant.PeerId == call.PeerId &&
                participant.PeerType == call.PeerType);
    }

    public static IEnumerable<long> GetCallUserRecipientIds(
        GroupCallDocument call,
        IEnumerable<long>? extraUserIds = null,
        bool includeInvited = false)
    {
        var ids = GetParticipantUserIds(call)
            .Append(call.CreatorId);

        if (includeInvited)
        {
            ids = ids.Concat(call.InvitedUserIds);
        }

        if (extraUserIds != null)
        {
            ids = ids.Concat(extraUserIds);
        }

        return ids.Where(id => id > 0).Distinct();
    }

    public static async Task PushUpdatesToUserIdsAsync(
        IObjectMessageSender objectMessageSender,
        IEnumerable<long> userIds,
        IUpdates updates,
        long? excludeUserId = null)
    {
        foreach (var userId in userIds.Where(id => id > 0).Distinct())
        {
            if (excludeUserId.HasValue && userId == excludeUserId.Value)
            {
                continue;
            }

            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates);
        }
    }

    public static async Task PushUpdatesToCallSubscribersAsync(
        IObjectMessageSender objectMessageSender,
        GroupCallDocument call,
        IUpdates updates,
        long? excludeUserId = null,
        IEnumerable<long>? extraUserIds = null)
    {
        await PushUpdatesToUserIdsAsync(
            objectMessageSender,
            GetCallUserRecipientIds(call, extraUserIds, includeInvited: true),
            updates,
            excludeUserId);

        if (call.PeerType != (int)PeerType.User)
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer((PeerType)call.PeerType, call.PeerId),
                updates,
                excludeUserId: excludeUserId);
        }
    }

    public static long CreateAccessHash()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        RandomNumberGenerator.Fill(bytes);
        var accessHash = BitConverter.ToInt64(bytes) & long.MaxValue;
        return accessHash == 0 ? 1 : accessHash;
    }

    public static TUpdateGroupCallConnection CreateConnectionUpdate(
        GroupCallDocument call,
        IReadOnlyCollection<WebRtcConnection>? webRtcConnections,
        bool presentation = false,
        bool streamFallback = false)
    {
        if (streamFallback)
        {
            // Stream mode: the client plays the call by downloading media chunks instead of using a live
            // WebRTC connection (see https://corefork.telegram.org/api/group-calls#detecting-stream-mode).
            //   * RTMP-mode calls (a single external publisher over RTMP)  -> {"stream": true, "rtmp": true}
            //   * automatically-scaled stream mode (large audiences)        -> {"stream": true}
            var streamParams = new Dictionary<string, object?>
            {
                ["stream"] = true
            };
            if (call.RtmpStream)
            {
                streamParams["rtmp"] = true;
            }

            var streamJson = System.Text.Json.JsonSerializer.Serialize(streamParams);

            return new TUpdateGroupCallConnection
            {
                Presentation = presentation,
                Params = new TDataJSON { Data = streamJson }
            };
        }

        var candidates = CreateConnectionCandidates(webRtcConnections);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Group call WebRTC candidates are not configured. Please configure App__WebRtcConnections in .env file.");
        }

        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["transport"] = new Dictionary<string, object?>
            {
                ["ufrag"] = CreateIceToken(call.CallId, "ufrag", 8),
                ["pwd"] = CreateIceToken(call.CallId, "pwd", 24),
                ["fingerprints"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["hash"] = "sha-256",
                        ["fingerprint"] = CreateFingerprint(call.CallId),
                        ["setup"] = "active"
                    }
                },
                ["candidates"] = candidates
            }
        });

        return new TUpdateGroupCallConnection
        {
            Presentation = presentation,
            Params = new TDataJSON { Data = json }
        };
    }

    public static int CreateParticipantSource(string? paramsJson, GroupCallDocument call, long userId)
    {
        var source = TryReadSsrc(paramsJson, true) ?? Random.Shared.Next(100000, 999999);
        if (call.Participants.Any(p =>
                !p.Left &&
                p.Source == source &&
                !IsParticipantControlledByUser(call, p, userId)))
        {
            RpcErrors.RpcErrors400.GroupcallSsrcDuplicateMuch.ThrowRpcError();
        }

        return source;
    }

    public static HashSet<int> GetForwardedSources(IEnumerable<GroupCallParticipantDoc> participants)
    {
        var sources = new HashSet<int>();
        foreach (var participant in participants)
        {
            sources.Add(participant.Source);
            AddSourcesFromParams(participant.ParamsJson, sources, false);
            AddSourcesFromParams(participant.PresentationParamsJson, sources, false);
        }

        return sources;
    }

    public static TUpdates Updates(params IUpdate[] updates)
    {
        return new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = CurrentDate()
        };
    }

    public static int CurrentDate()
    {
        return (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static int ParseOffset(string? offset)
    {
        return int.TryParse(offset, out var value) && value > 0 ? value : 0;
    }

    public static string CreateInviteHash()
    {
        return Guid.NewGuid().ToString("N")[..22];
    }

    private static int? TryReadSsrc(string? paramsJson, bool throwOnInvalid)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(paramsJson);
            if (document.RootElement.TryGetProperty("ssrc", out var ssrc) &&
                ssrc.ValueKind == JsonValueKind.Number &&
                ssrc.TryGetInt32(out var value) &&
                value > 0)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            if (throwOnInvalid)
            {
                RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            }
        }

        return null;
    }

    private static void AddSourcesFromParams(string? paramsJson, ISet<int> sources, bool throwOnInvalid)
    {
        if (string.IsNullOrWhiteSpace(paramsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(paramsJson);
            AddPositiveIntProperty(document.RootElement, "ssrc", sources);
            if (!document.RootElement.TryGetProperty("ssrc-groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("sources", out var groupSources) ||
                    groupSources.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var source in groupSources.EnumerateArray())
                {
                    AddPositiveInt(source, sources);
                }
            }
        }
        catch (JsonException)
        {
            if (throwOnInvalid)
            {
                RpcErrors.RpcErrors400.DataJsonInvalid.ThrowRpcError();
            }
        }
    }

    private static void AddPositiveIntProperty(JsonElement element, string propertyName, ISet<int> values)
    {
        if (element.TryGetProperty(propertyName, out var value))
        {
            AddPositiveInt(value, values);
        }
    }

    private static void AddPositiveInt(JsonElement element, ISet<int> values)
    {
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var value) &&
            value > 0)
        {
            values.Add(value);
        }
    }

    private static List<Dictionary<string, string>> CreateConnectionCandidates(IReadOnlyCollection<WebRtcConnection>? webRtcConnections)
    {
        var candidates = new List<Dictionary<string, string>>();
        if (webRtcConnections == null)
        {
            return candidates;
        }

        var id = 1;
        foreach (var connection in webRtcConnections.Where(c => c.Port > 0 && !string.IsNullOrWhiteSpace(c.Ip)))
        {
            var candidateId = id.ToString();
            candidates.Add(new Dictionary<string, string>
            {
                ["port"] = connection.Port.ToString(),
                ["protocol"] = "udp",
                ["network"] = candidateId,
                ["generation"] = "0",
                ["id"] = candidateId,
                ["component"] = "1",
                ["foundation"] = candidateId,
                ["priority"] = Math.Max(1, 2130706431 - id).ToString(),
                ["ip"] = connection.Ip,
                ["type"] = "host"
            });
            id++;
        }

        return candidates;
    }

    private static string CreateIceToken(long callId, string purpose, int length)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"testgram-group-call:{callId}:{purpose}"));
        return Convert.ToHexString(hash)[..length].ToLowerInvariant();
    }

    private static string CreateFingerprint(long callId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"testgram-group-call:{callId}:fingerprint"));
        return string.Join(':', hash.Select(value => value.ToString("X2")));
    }

    public static IPeer ToPeer(PeerType peerType, long peerId)
    {
        return peerType switch
        {
            PeerType.Chat => new TPeerChat { ChatId = peerId },
            PeerType.Channel => new TPeerChannel { ChannelId = peerId },
            _ => new TPeerUser { UserId = peerId }
        };
    }
}
