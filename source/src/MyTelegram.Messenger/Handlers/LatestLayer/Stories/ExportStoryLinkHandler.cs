using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class ExportStoryLinkHandler(
    IUserAppService userAppService,
    IChannelAppService channelAppService)
    : RpcResultObjectHandler<RequestExportStoryLink, IExportedStoryLink>
{
    protected override async Task<IExportedStoryLink> HandleCoreAsync(IRequestInput input, RequestExportStoryLink obj)
    {
        if (obj.Id <= 0)
        {
            RpcErrors.RpcErrors400.StoryIdEmpty.ThrowRpcError();
            return null!;
        }

        var username = await GetPublicUsernameAsync(obj.Peer, input.UserId);
        if (string.IsNullOrWhiteSpace(username))
        {
            RpcErrors.RpcErrors400.UserPublicMissing.ThrowRpcError();
            return null!;
        }

        var link = $"https://t.me/{username}/s/{obj.Id}";

        return new TExportedStoryLink
        {
            Link = link
        };
    }

    private async Task<string?> GetPublicUsernameAsync(IInputPeer peer, long selfUserId)
    {
        switch (peer)
        {
            case TInputPeerSelf:
                return (await userAppService.GetAsync(selfUserId))?.UserName;

            case TInputPeerUser user:
                return (await userAppService.GetAsync(user.UserId))?.UserName;

            case TInputPeerChannel channel:
                return (await channelAppService.GetAsync(channel.ChannelId))?.UserName;

            default:
                return null;
        }
    }
}
