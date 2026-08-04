using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

public interface IStoryUpdatesSender
{
    /// <summary>
    /// Delivers a story update to everyone who can see the story: the owner's contacts for user
    /// stories (privacy-filtered), or the channel's subscribers for channel stories.
    /// </summary>
    Task PushStoryUpdateAsync(StoryDocument story, IUpdates updates, long? excludeUserId = null);

    /// <summary>Delivers an update to a single user's own sessions.</summary>
    Task PushToUserAsync(long userId, IUpdates updates, long? excludeAuthKeyId = null);
}

/// <summary>
/// Pushes story-related updates to the peers that need them. Without this, a new or changed story only
/// becomes visible after the client next polls stories.getAllStories.
/// <para>Mirrors the group-call pattern in <c>Services/Phone/GroupCallStateHelper</c>.</para>
/// </summary>
public class StoryUpdatesSender(
    IMongoDatabase mongoDatabase,
    IObjectMessageSender objectMessageSender,
    IQueryProcessor queryProcessor,
    IStoryAccessService storyAccessService)
    : IStoryUpdatesSender, ITransientDependency
{
    private readonly IMongoCollection<CloseFriendDocument> _closeFriendCollection =
        mongoDatabase.GetCollection<CloseFriendDocument>("close_friends");

    public async Task PushStoryUpdateAsync(StoryDocument story, IUpdates updates, long? excludeUserId = null)
    {
        if (story.OwnerPeerType == StoryHelper.PeerTypeChannel)
        {
            await objectMessageSender.PushMessageToPeerAsync(
                new Peer(PeerType.Channel, story.OwnerPeerId),
                updates,
                excludeUserId: excludeUserId);
            return;
        }

        if (story.OwnerPeerType != StoryHelper.PeerTypeUser)
        {
            return;
        }

        var recipientIds = await GetUserStoryRecipientIdsAsync(story);

        foreach (var userId in recipientIds)
        {
            if (excludeUserId.HasValue && userId == excludeUserId.Value)
            {
                continue;
            }

            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, userId), updates);
        }
    }

    public Task PushToUserAsync(long userId, IUpdates updates, long? excludeAuthKeyId = null)
    {
        return objectMessageSender.PushMessageToPeerAsync(
            new Peer(PeerType.User, userId),
            updates,
            excludeAuthKeyId: excludeAuthKeyId);
    }

    /// <summary>
    /// Candidate viewers of a user story: the owner's own sessions plus the owner's contacts, minus
    /// anyone the story's privacy rules exclude.
    /// </summary>
    private async Task<List<long>> GetUserStoryRecipientIdsAsync(StoryDocument story)
    {
        var ownerId = story.OwnerPeerId;
        var contactIds = await queryProcessor.ProcessAsync(new GetContactUserIdListQuery(ownerId));

        var recipients = new List<long> { ownerId };

        if (contactIds.Count == 0)
        {
            return recipients;
        }

        var closeFriendDoc = await _closeFriendCollection
            .Find(p => p.SelfUserId == ownerId)
            .FirstOrDefaultAsync();

        var closeFriendIds = closeFriendDoc?.UserIds ?? [];

        foreach (var contactId in contactIds.Distinct())
        {
            if (contactId == ownerId || contactId <= 0)
            {
                continue;
            }

            // Contacts of the owner are, by definition, contacts for privacy purposes; close-friend
            // membership comes from the owner's list.
            var context = new StoryViewerContext
            {
                UserId = contactId,
                OwnersWhoHaveViewerAsContact = [ownerId],
                OwnersWhoHaveViewerAsCloseFriend = closeFriendIds.Contains(contactId) ? [ownerId] : []
            };

            if (StoryHelper.CanViewStory(story, contactId, context))
            {
                recipients.Add(contactId);
            }
        }

        return recipients;
    }
}
