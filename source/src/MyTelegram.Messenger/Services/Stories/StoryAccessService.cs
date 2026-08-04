using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Single place where story access is decided.
/// <para>
/// Every mutating stories.* handler must resolve its target peer through
/// <see cref="ResolveOwnedPeerAsync"/>: <see cref="StoryHelper.ResolvePeer"/> alone trusts whatever peer
/// the client sent, which would let a caller post, edit or delete stories as another peer.
/// </para>
/// </summary>
public class StoryAccessService(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker)
    : IStoryAccessService, ITransientDependency
{
    private readonly IMongoCollection<CloseFriendDocument> _closeFriendCollection =
        mongoDatabase.GetCollection<CloseFriendDocument>("close_friends");
    private readonly IMongoCollection<StoryStealthDocument> _stealthCollection =
        mongoDatabase.GetCollection<StoryStealthDocument>("story_stealth_modes");

    public async Task<(long peerId, int peerType)> ResolveOwnedPeerAsync(
        IInputPeer? peer,
        long userId,
        StoryRight right)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(peer, userId);

        switch (peerType)
        {
            case StoryHelper.PeerTypeUser:
                // A user's stories can only be managed by that user. Business-connection posting on
                // behalf of another user is not supported here, so any other user id is a bad peer.
                if (peerId != userId)
                {
                    RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                }
                break;

            case StoryHelper.PeerTypeChannel:
                // HasChatAdminRightAsync resolves the channel with the throwing overload, so validate
                // existence first to surface a proper RPC error instead of an ArgumentException.
                var postChannel = await channelAppService.GetAsync((long?)peerId);
                if (postChannel == null)
                {
                    RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                }

                await channelAdminRightsChecker.CheckAdminRightAsync(
                    peerId,
                    userId,
                    RightSelector(right));
                break;

            default:
                // Basic groups cannot own stories.
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                break;
        }

        return (peerId, peerType);
    }

    public async Task<bool> CanActAsPeerAsync(long peerId, int peerType, long userId, StoryRight right)
    {
        return peerType switch
        {
            StoryHelper.PeerTypeUser => peerId == userId,
            StoryHelper.PeerTypeChannel => await channelAdminRightsChecker.HasChatAdminRightAsync(
                peerId, userId, RightSelector(right)),
            _ => false
        };
    }

    public async Task<(long peerId, int peerType)> ResolveReadablePeerAsync(IInputPeer? peer, long userId)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(peer, userId);

        switch (peerType)
        {
            case StoryHelper.PeerTypeUser:
                if (peerId != userId)
                {
                    var userReadModel = await userAppService.GetAsync((long?)peerId);
                    if (userReadModel == null)
                    {
                        RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                    }
                }
                break;

            case StoryHelper.PeerTypeChannel:
                var channelReadModel = await channelAppService.GetAsync((long?)peerId);
                if (channelReadModel == null)
                {
                    RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
                }
                break;

            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                break;
        }

        return (peerId, peerType);
    }

    public async Task<StoryViewerContext> GetViewerContextAsync(long userId, IEnumerable<long>? ownerUserIds = null)
    {
        var userReadModel = await userAppService.GetAsync((long?)userId);
        var stealth = await _stealthCollection.Find(p => p.UserId == userId).FirstOrDefaultAsync();

        var owners = ownerUserIds?.Where(id => id > 0 && id != userId).Distinct().ToList() ?? [];

        if (owners.Count == 0)
        {
            return new StoryViewerContext
            {
                UserId = userId,
                IsPremium = userReadModel?.Premium ?? false,
                StealthMode = stealth
            };
        }

        // "Contacts only" refers to the story owner's contact list, so look for contact rows where the
        // owner is the self side and the viewer is the target.
        var ownersWithViewerAsContact = await queryProcessor.ProcessAsync(
            new GetContactUserIdListByTargetUserIdListQuery(userId, owners));

        var closeFriendDocs = await _closeFriendCollection
            .Find(Builders<CloseFriendDocument>.Filter.In(p => p.SelfUserId, owners))
            .ToListAsync();

        var ownersWithViewerAsCloseFriend = closeFriendDocs
            .Where(d => d.UserIds.Contains(userId))
            .Select(d => d.SelfUserId)
            .ToHashSet();

        return new StoryViewerContext
        {
            UserId = userId,
            IsPremium = userReadModel?.Premium ?? false,
            OwnersWhoHaveViewerAsContact = ownersWithViewerAsContact.ToHashSet(),
            OwnersWhoHaveViewerAsCloseFriend = ownersWithViewerAsCloseFriend,
            StealthMode = stealth
        };
    }

    public List<StoryDocument> FilterVisible(
        IEnumerable<StoryDocument> stories,
        long userId,
        StoryViewerContext context)
    {
        return stories.Where(story => StoryHelper.CanViewStory(story, userId, context)).ToList();
    }

    private static Func<ChatAdminRights, bool> RightSelector(StoryRight right)
    {
        return right switch
        {
            StoryRight.Post => rights => rights.PostStories,
            StoryRight.Edit => rights => rights.EditStories,
            StoryRight.Delete => rights => rights.DeleteStories,
            _ => rights => rights.PostStories
        };
    }
}
