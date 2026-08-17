namespace MyTelegram.Domain.Sagas.Events;

public class InviteToChannelSagaStartSagaEvent(
    RequestInfo requestInfo,
    long channelId,
    bool broadcast,
    bool hasLink,
    long inviterId,
    IReadOnlyCollection<long> memberUserIds,
    IReadOnlyCollection<long> botUserIds,
    int channelHistoryMinId,
    int maxMessageId,
    ChatJoinType chatJoinType,
    IReadOnlyCollection<long> missingInviteeUserIds
    )
    : RequestAggregateEvent2<InviteToChannelSaga, InviteToChannelSagaId>(requestInfo)
{
    /// <summary>
    /// Invitees dropped because their privacy settings do not allow it. They are reported back to
    /// the caller as <c>missingInvitee</c> instead of failing the whole request.
    /// </summary>
    public IReadOnlyCollection<long> MissingInviteeUserIds { get; } = missingInviteeUserIds;

    public IReadOnlyCollection<long> BotUserIds { get; } = botUserIds;
    public bool Broadcast { get; } = broadcast;
    public int ChannelHistoryMinId { get; } = channelHistoryMinId;
    public long ChannelId { get; } = channelId;
    public long InviterId { get; } = inviterId;
    public int MaxMessageId { get; } = maxMessageId;
    public ChatJoinType ChatJoinType { get; } = chatJoinType;
    public bool HasLink { get; } = hasLink;
    public IReadOnlyCollection<long> MemberUserIds { get; } = memberUserIds;
}
