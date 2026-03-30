using System.Text.Json;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetCallConfigHandler
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetCallConfig, IDataJSON>
{
    protected override Task<IDataJSON> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetCallConfig obj)
    {
        var config = new
        {
            iceServers = new[]
            {
                new
                {
                    urls = new[] { "stun:stun.l.google.com:19302" },
                    username = "",
                    credential = ""
                }
            },
            defaultProtocol = "udp",
            udpP2P = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        return Task.FromResult<IDataJSON>(new TDataJSON { Data = json });
    }
}
