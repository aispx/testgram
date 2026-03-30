using MyTelegram.Schema.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

internal sealed class GetPreviewInfoHandler : RpcResultObjectHandler<RequestGetPreviewInfo, IPreviewInfo>
{
    protected override Task<IPreviewInfo> HandleCoreAsync(IRequestInput input, RequestGetPreviewInfo obj)
        => Task.FromResult<IPreviewInfo>(new TPreviewInfo { Media = [], LangCodes = [] });
}
