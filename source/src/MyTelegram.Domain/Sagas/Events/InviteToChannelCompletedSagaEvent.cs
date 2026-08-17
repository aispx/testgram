namespace MyTelegram.Domain.Sagas.Events;

public class InviteToChannelCompletedSagaEvent(
    RequestInfo requestInfo,
    long channelId,
    long inviterId,
    bool broadcast,
    IReadOnlyCollection<long> memberUserIds,
    IReadOnlyCollection<long> botUserIds,
    bool hasLink,
    ChatJoinType chatJoinType,
    IReadOnlyCollection<long> missingInviteeUserIds
    )
    : RequestAggregateEvent2<InviteToChannelSaga, InviteToChannelSagaId>(requestInfo)
{
    /// <summary>
    /// Invitees dropped because their privacy settings do not allow it; reported back to the
    /// caller as <c>missingInvitee</c> entries.
    /// </summary>
    public IReadOnlyCollection<long> MissingInviteeUserIds { get; } = missingInviteeUserIds;

    public long ChannelId { get; } = channelId;
    public long InviterId { get; } = inviterId;
    public bool Broadcast { get; } = broadcast;
    public IReadOnlyCollection<long> MemberUserIds { get; } = memberUserIds;
    public IReadOnlyCollection<long> BotUserIds { get; } = botUserIds;
    public bool HasLink { get; } = hasLink;
    public ChatJoinType ChatJoinType { get; } = chatJoinType;
}
