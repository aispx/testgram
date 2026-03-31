using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

internal sealed class GetBotCallbackAnswerHandler(IXieFatherBotService xieFatherBotService, IPeerHelper peerHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer, MyTelegram.Schema.Messages.IBotCallbackAnswer>
{
    protected override async Task<MyTelegram.Schema.Messages.IBotCallbackAnswer> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetBotCallbackAnswer obj)
    {
        Console.WriteLine($"[GetBotCallbackAnswer] Called: userId={input.UserId}, msgId={obj.MsgId}, hasData={obj.Data.HasValue}");

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        Console.WriteLine($"[GetBotCallbackAnswer] Peer: peerId={peer.PeerId}, peerType={peer.PeerType}");

        if (peer.PeerId == XieFatherBotService.BotUserId && obj.Data.HasValue)
        {
            var data = System.Text.Encoding.UTF8.GetString(obj.Data.Value.Span);
            Console.WriteLine($"[GetBotCallbackAnswer] XieFather callback data: {data}");
            _ = Task.Run(() => xieFatherBotService.HandleCallbackAsync(input, input.UserId, obj.MsgId, data));
        }

        return new MyTelegram.Schema.Messages.TBotCallbackAnswer { CacheTime = 0 };
    }
}
