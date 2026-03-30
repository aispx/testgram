namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
internal sealed class ExportMessageLinkHandler(IPeerHelper peerHelper, IChannelAppService channelAppService) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestExportMessageLink, MyTelegram.Schema.IExportedMessageLink>
{
    protected override async Task<IExportedMessageLink> HandleCoreAsync(IRequestInput input, RequestExportMessageLink obj)
    {
        var peer = peerHelper.GetChannel(obj.Channel);
        var channel = await channelAppService.GetAsync(peer.PeerId);
        var username = channel?.UserName;
        var link = !string.IsNullOrEmpty(username)
            ? $"https://t.me/{username}/{obj.Id}"
            : $"https://t.me/c/{peer.PeerId}/{obj.Id}";
        return new TExportedMessageLink { Link = link, Html = string.Empty };
    }
}
