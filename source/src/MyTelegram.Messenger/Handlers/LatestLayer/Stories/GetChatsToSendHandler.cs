using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Obtain a list of channels where the user can post <a href="https://corefork.telegram.org/api/stories">stories</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.getChatsToSend"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// Membership alone is not enough: a channel is only offered when the caller actually holds the
/// post-stories admin right there.
/// </para>
/// </remarks>
internal sealed class GetChatsToSendHandler(
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService,
    IStoryAccessService storyAccessService)
    : RpcResultObjectHandler<MyTelegram.Schema.Stories.RequestGetChatsToSend, IChats>
{
    protected override async Task<IChats> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Stories.RequestGetChatsToSend obj)
    {
        var channelIds = await queryProcessor.ProcessAsync(new GetChannelIdListByUserIdQuery(input.UserId));

        var allowedChannelIds = new List<long>();
        foreach (var channelId in channelIds.Distinct())
        {
            if (await storyAccessService.CanActAsPeerAsync(
                    channelId, StoryHelper.PeerTypeChannel, input.UserId, StoryRight.Post))
            {
                allowedChannelIds.Add(channelId);
            }
        }

        if (allowedChannelIds.Count == 0)
        {
            return new TChats { Chats = new TVector<IChat>() };
        }

        var channelMemberReadModels = await queryProcessor.ProcessAsync(
            new GetChannelMemberListByChannelIdListQuery(input.UserId, allowedChannelIds));

        var chats = await chatConverterService.GetChannelListAsync(
            input,
            allowedChannelIds,
            channelMemberReadModels,
            input.Layer);

        return new TChats { Chats = new TVector<IChat>(chats) };
    }
}
