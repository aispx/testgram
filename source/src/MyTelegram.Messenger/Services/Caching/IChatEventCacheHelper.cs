namespace MyTelegram.Messenger.Services.Caching;
public interface IChatEventCacheHelper
{
    void Add(ChannelCreatedEvent data);
    void Add(long chatId, long migrateToChannelId);

    bool TryRemoveMigrateChannelId(long chatId, out long migrateToChannelId);
    bool TryGetMigrateChannelId(long chatId, out long migrateToChannelId);
    bool TryRemoveChannelCreatedEvent(long channelId,
        [NotNullWhen(true)] out ChannelCreatedEvent? channelCreatedEvent);

    /// <summary>
    /// Records the invitees that were dropped because of their privacy settings, so the
    /// <c>messages.invitedUsers</c> reply assembled later by the message pipeline can report them
    /// as <c>missingInvitee</c> entries. Keyed by the originating request.
    /// See https://corefork.telegram.org/api/invites#direct-invites
    /// </summary>
    void AddMissingInvitees(Guid requestId, IReadOnlyCollection<long> userIds);

    bool TryRemoveMissingInvitees(Guid requestId,
        [NotNullWhen(true)] out IReadOnlyCollection<long>? userIds);
}