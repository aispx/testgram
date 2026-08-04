using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Check whether we can post stories as the specified peer, and how many more we may post right now.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 STORIES_TOO_MUCH You have hit the maximum active stories limit.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.canSendStory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CanSendStoryHandler(
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IStoryAccessService storyAccessService,
    IStoryConfigProvider storyConfigProvider)
    : RpcResultObjectHandler<RequestCanSendStory, ICanSendStoryCount>
{
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");

    protected override async Task<ICanSendStoryCount> HandleCoreAsync(IRequestInput input, RequestCanSendStory obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);

        // Same right as sendStory, but reported as a zero allowance rather than an error.
        if (!await storyAccessService.CanActAsPeerAsync(peerId, peerType, input.UserId, StoryRight.Post))
        {
            return new TCanSendStoryCount { CountRemains = 0 };
        }

        var userReadModel = await userAppService.GetAsync((long?)input.UserId);
        var limit = storyConfigProvider.GetExpiringLimit(userReadModel?.Premium ?? false);

        var currentDate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var activeCount = await _storyCollection.CountDocumentsAsync(
            Builders<StoryDocument>.Filter.And(
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, peerId),
                Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, peerType),
                Builders<StoryDocument>.Filter.Eq(s => s.Deleted, false),
                Builders<StoryDocument>.Filter.Gte(s => s.ExpireDate, currentDate)));

        return new TCanSendStoryCount
        {
            CountRemains = Math.Max(0, limit - (int)activeCount)
        };
    }
}
