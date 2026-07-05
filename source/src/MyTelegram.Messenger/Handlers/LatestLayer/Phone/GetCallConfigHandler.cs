using System.Text.Json;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetCallConfigHandler(
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetCallConfig, IDataJSON>
{
    protected override Task<IDataJSON> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetCallConfig obj)
    {
        // Requirement 1.3: an unauthorized session (auth key not bound to a user) must be rejected.
        if (input.UserId == 0)
        {
            RpcErrors.RpcErrors401.AuthKeyUnregistered.ThrowRpcError();
        }

        var iceServers = new List<object>();

        var webRtcConnections = options.CurrentValue.WebRtcConnections;

        if (webRtcConnections != null && webRtcConnections.Count > 0)
        {
            foreach (var webRtcConfig in webRtcConnections)
            {
                if (webRtcConfig.Stun)
                {
                    iceServers.Add(new
                    {
                        urls = new[] { $"stun:{webRtcConfig.Ip}:{webRtcConfig.Port}" },
                        username = "",
                        credential = ""
                    });
                }

                if (webRtcConfig.Turn)
                {
                    iceServers.Add(new
                    {
                        urls = new[] {
                            $"turn:{webRtcConfig.Ip}:{webRtcConfig.Port}?transport=udp",
                            $"turn:{webRtcConfig.Ip}:{webRtcConfig.Port}?transport=tcp"
                        },
                        username = webRtcConfig.UserName,
                        credential = webRtcConfig.Password
                    });
                }
            }
        }

        if (iceServers.Count == 0)
        {
            throw new InvalidOperationException("WebRTC connections not configured. Please configure App__WebRtcConnections in .env file.");
        }

        var config = new
        {
            iceServers = iceServers.ToArray(),
            defaultProtocol = "udp",
            udpP2P = true,
            udpReflector = true,
            minLayer = 65,
            maxLayer = 92
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        return Task.FromResult<IDataJSON>(new TDataJSON { Data = json });
    }
}
