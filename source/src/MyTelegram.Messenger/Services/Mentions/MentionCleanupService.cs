using EventFlow.Exceptions;

namespace MyTelegram.Messenger.Services.Mentions;

/// <summary>
/// Keeps the @ badge honest when messages disappear: a deleted message that still held an unread
/// mention has to give its point back, otherwise the dialog counter stays above the number of
/// mentions the user can actually reach. See https://corefork.telegram.org/api/mentions
/// </summary>
public interface IMentionCleanupService
{
    Task OnMessagesDeletedAsync(IReadOnlyCollection<IMessageReadModel> messages);
}

public class MentionCleanupService(
    ICommandBus commandBus,
    IMentionReadStateService mentionReadStateService,
    ILogger<MentionCleanupService> logger) : IMentionCleanupService, ITransientDependency
{
    public async Task OnMessagesDeletedAsync(IReadOnlyCollection<IMessageReadModel> messages)
    {
        // (mentioned user, dialog) -> ids that were about to vanish from their unread mentions.
        var byDialog = new Dictionary<(long UserId, Peer Peer), List<int>>();

        foreach (var message in messages)
        {
            if (!(message.MentionedUserIds?.Count > 0))
            {
                continue;
            }

            var peer = new Peer(message.ToPeerType, message.ToPeerId);

            foreach (var mentionedUserId in message.MentionedUserIds)
            {
                // A channel keeps a single copy owned by the channel; a private chat keeps one copy
                // per side, and only the mentioned user's own copy carries their badge. Skipping the
                // sender's copy is what stops the counter from being decremented twice.
                if (message.ToPeerType != PeerType.Channel && message.OwnerPeerId != mentionedUserId)
                {
                    continue;
                }

                if (!byDialog.TryGetValue((mentionedUserId, peer), out var ids))
                {
                    ids = [];
                    byDialog[(mentionedUserId, peer)] = ids;
                }

                ids.Add(message.MessageId);
            }
        }

        foreach (var ((userId, peer), messageIds) in byDialog)
        {
            var state = await mentionReadStateService.GetAsync(userId, peer);
            var unread = messageIds.Where(p => IMentionReadStateService.IsUnread(state, p)).ToList();
            if (unread.Count == 0)
            {
                continue;
            }

            // Marking them read as well keeps the state consistent if the same id is ever reused.
            await mentionReadStateService.MarkReadAsync(userId, peer, unread);

            foreach (var messageId in unread)
            {
                try
                {
                    await commandBus.PublishAsync(new ReadMentionCommand(DialogId.Create(userId, peer), messageId));
                }
                catch (DomainError)
                {
                    logger.LogDebug("No dialog aggregate for user {UserId} and peer {Peer}, mention counter left alone",
                        userId, peer);
                }
            }
        }
    }
}
