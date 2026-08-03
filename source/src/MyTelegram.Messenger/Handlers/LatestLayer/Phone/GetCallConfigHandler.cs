using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

/// <summary>
/// Returns the tgcalls runtime configuration blob.
/// See https://core.telegram.org/method/phone.getCallConfig
/// </summary>
/// <remarks>
/// The payload is consumed by tgcalls' server config (the Android client parses it in
/// <c>Instance.ServerConfig</c>), which looks up snake_case keys such as <c>use_system_aec</c> and
/// <c>enable_vp8_encoder</c>; unrecognised keys are ignored. The codec keys are what gate video codec
/// availability, so they matter for video calls.
/// <para>
/// This must not fail: TDLib issues <c>phone.getCallConfig</c> from <c>CallActor::start_up</c> and
/// discards the call if it errors, so an unconfigured server would break calls entirely rather than
/// degrade them. Media endpoints are unrelated to this method - they are handed out as
/// <c>phoneConnectionWebrtc</c> by <c>phone.confirmCall</c>.
/// </para>
/// </remarks>
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

        var runtimeConfig = options.CurrentValue.Calls?.RuntimeConfig ?? new CallRuntimeConfig();

        // Built as a JsonObject rather than serialising a POCO: the key names are dictated by tgcalls and
        // do not follow any C# naming convention, and this keeps the handler free of reflection-based
        // serialization.
        var config = new JsonObject
        {
            ["use_system_ns"] = runtimeConfig.UseSystemNs,
            ["use_system_aec"] = runtimeConfig.UseSystemAec,
            ["voip_enable_stun_marking"] = runtimeConfig.EnableStunMarking,
            ["hangup_ui_timeout"] = runtimeConfig.HangupUiTimeout,
            ["enable_vp8_encoder"] = runtimeConfig.EnableVp8Encoder,
            ["enable_vp8_decoder"] = runtimeConfig.EnableVp8Decoder,
            ["enable_vp9_encoder"] = runtimeConfig.EnableVp9Encoder,
            ["enable_vp9_decoder"] = runtimeConfig.EnableVp9Decoder,
            ["enable_h264_encoder"] = runtimeConfig.EnableH264Encoder,
            ["enable_h264_decoder"] = runtimeConfig.EnableH264Decoder,
            ["enable_h265_encoder"] = runtimeConfig.EnableH265Encoder,
            ["enable_h265_decoder"] = runtimeConfig.EnableH265Decoder
        };

        return Task.FromResult<IDataJSON>(new TDataJSON { Data = config.ToJsonString() });
    }
}
