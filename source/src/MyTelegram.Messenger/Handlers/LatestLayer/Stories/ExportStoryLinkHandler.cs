using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Generate a <a href="https://corefork.telegram.org/api/links#story-links">story deep link</a> for a specific story.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORY_ID_EMPTY You specified no story IDs.
/// 400 USER_PUBLIC_MISSING You must set a username for the current user.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.exportStoryLink"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// A story link is only meaningful for a peer with a public username, since that is what the link
/// resolves through.
/// </para>
/// </remarks>
internal sealed class ExportStoryLinkHandler(
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<RequestExportStoryLink, IExportedStoryLink>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<IExportedStoryLink> HandleCoreAsync(IRequestInput input, RequestExportStoryLink obj)
    {
        if (obj.Id <= 0)
        {
            RpcErrors.RpcErrors400.StoryIdEmpty.ThrowRpcError();
        }

        var (peerId, peerType) = await storyAccessService.ResolveReadablePeerAsync(obj.Peer, input.UserId);

        var story = await _storyCollection
            .Find(Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.Eq(s => s.StoryId, obj.Id),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false)))
            .FirstOrDefaultAsync();

        if (story == null)
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var context = await storyAccessService.GetViewerContextAsync(input.UserId, [peerId]);
        if (!StoryHelper.CanViewStory(story!, input.UserId, context))
        {
            RpcErrors.RpcErrors400.StoryIdInvalid.ThrowRpcError();
        }

        var username = await GetPublicUsernameAsync(peerId, peerType);
        if (string.IsNullOrWhiteSpace(username))
        {
            RpcErrors.RpcErrors400.UserPublicMissing.ThrowRpcError();
        }

        return new TExportedStoryLink
        {
            Link = $"https://t.me/{username}/s/{obj.Id}"
        };
    }

    private async Task<string?> GetPublicUsernameAsync(long peerId, int peerType)
    {
        return peerType switch
        {
            StoryHelper.PeerTypeChannel => (await channelAppService.GetAsync((long?)peerId))?.UserName,
            StoryHelper.PeerTypeUser => (await userAppService.GetAsync((long?)peerId))?.UserName,
            _ => null
        };
    }
}
