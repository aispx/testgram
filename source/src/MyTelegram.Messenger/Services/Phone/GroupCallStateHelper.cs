using MongoDB.Driver;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Services.Phone;

internal static class GroupCallStateHelper
{
    public static FilterDefinition<GroupCallDocument> Filter(TInputGroupCall call)
    {
        return Builders<GroupCallDocument>.Filter.And(
            Builders<GroupCallDocument>.Filter.Eq(g => g.CallId, call.Id),
            Builders<GroupCallDocument>.Filter.Eq(g => g.AccessHash, call.AccessHash));
    }

    public static TInputGroupCall ToInputGroupCall(GroupCallDocument call)
    {
        return new TInputGroupCall
        {
            Id = call.CallId,
            AccessHash = call.AccessHash
        };
    }

    public static TGroupCall ToGroupCall(GroupCallDocument call, long selfUserId)
    {
        return new TGroupCall
        {
            Id = call.CallId,
            AccessHash = call.AccessHash,
            ParticipantsCount = call.Participants.Count,
            Title = call.Title,
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
            UnmutedVideoCount = call.Participants.Count(p => !p.VideoStopped),
            UnmutedVideoLimit = 100,
            InviteLink = call.Conference ? call.InviteLink : null,
            SendPaidMessagesStars = call.SendPaidMessagesStars,
            DefaultSendAs = call.DefaultSendAsPeerId.HasValue && call.DefaultSendAsPeerType.HasValue
                ? ToPeer((PeerType)call.DefaultSendAsPeerType.Value, call.DefaultSendAsPeerId.Value)
                : null
        };
    }

    public static TGroupCallDiscarded ToDiscardedGroupCall(GroupCallDocument call, int date)
    {
        return new TGroupCallDiscarded
        {
            Id = call.CallId,
            AccessHash = call.AccessHash,
            Duration = Math.Max(0, date - call.Date)
        };
    }

    public static TGroupCallParticipant ToParticipant(
        GroupCallParticipantDoc participant,
        long selfUserId,
        IPeerHelper peerHelper)
    {
        return new TGroupCallParticipant
        {
            Peer = peerHelper.ToPeer((PeerType)participant.PeerType, participant.PeerId),
            Source = participant.Source,
            Date = participant.Date,
            ActiveDate = participant.Date,
            Muted = participant.Muted,
            CanSelfUnmute = true,
            Self = participant.PeerId == selfUserId,
            VideoJoined = !participant.VideoStopped,
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
            Peer = peerHelper.ToPeer((PeerType)call.PeerType, call.PeerId),
            Call = ToGroupCall(call, selfUserId)
        };
    }

    public static TUpdateGroupCallParticipants CreateParticipantsUpdate(
        GroupCallDocument call,
        long selfUserId,
        IPeerHelper peerHelper,
        IEnumerable<GroupCallParticipantDoc> participants)
    {
        return new TUpdateGroupCallParticipants
        {
            Call = ToInputGroupCall(call),
            Participants = new TVector<IGroupCallParticipant>(
                participants.Select(p => (IGroupCallParticipant)ToParticipant(p, selfUserId, peerHelper))),
            Version = call.Version
        };
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

    private static IPeer ToPeer(PeerType peerType, long peerId)
    {
        return peerType switch
        {
            PeerType.Chat => new TPeerChat { ChatId = peerId },
            PeerType.Channel => new TPeerChannel { ChannelId = peerId },
            _ => new TPeerUser { UserId = peerId }
        };
    }
}
