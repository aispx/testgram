using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

internal sealed class ExportStoryLinkHandler : RpcResultObjectHandler<RequestExportStoryLink, IExportedStoryLink>
{
    protected override Task<IExportedStoryLink> HandleCoreAsync(IRequestInput input, RequestExportStoryLink obj)
    {
        var (peerId, peerType) = StoryHelper.ResolvePeer(obj.Peer, input.UserId);
        var link = $"https://testgram.b2t.pro/story/{peerId}/{obj.Id}";

        return Task.FromResult<IExportedStoryLink>(new TExportedStoryLink
        {
            Link = link
        });
    }
}
