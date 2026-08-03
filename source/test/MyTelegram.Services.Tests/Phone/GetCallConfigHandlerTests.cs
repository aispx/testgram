using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Unit tests for <c>GetCallConfigHandler</c> (<c>phone.getCallConfig</c>).
///
/// Covers Requirement 1:
///   * 1.1 - a <c>dataJSON</c> object carrying the tgcalls call-configuration parameters is returned,
///           under the snake_case key names tgcalls actually reads (Instance.ServerConfig on Android).
///   * 1.2 - the configuration is consistent: the same server options always yield the same config,
///           and the configured values are reflected in the payload.
///   * 1.3 - an unauthorized session (auth key not bound to a user, <c>UserId == 0</c>) is rejected
///           with <c>AUTH_KEY_UNREGISTERED</c>.
/// </summary>
public class GetCallConfigHandlerTests
{
    private const long AuthorizedUserId = 42;

    [Fact]
    public async Task GetCallConfig_ReturnsDataJsonWithExpectedShape()
    {
        var handler = CreateHandler(DefaultOptions());

        var dataJson = await InvokeAsync(handler, AuthorizedUserId);

        // R1.1: a dataJSON object is returned carrying a non-empty JSON payload.
        var tDataJson = dataJson.ShouldBeOfType<TDataJSON>();
        tDataJson.Data.ShouldNotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(tDataJson.Data);
        var root = doc.RootElement;

        // R1.1: the keys tgcalls looks up (Instance.ServerConfig in the Android client) are present,
        // defaulting to the same values the clients fall back to.
        root.GetProperty("use_system_ns").GetBoolean().ShouldBeTrue();
        root.GetProperty("use_system_aec").GetBoolean().ShouldBeTrue();
        root.GetProperty("voip_enable_stun_marking").GetBoolean().ShouldBeFalse();
        root.GetProperty("hangup_ui_timeout").GetDouble().ShouldBe(5);

        foreach (var codecKey in new[]
                 {
                     "enable_vp8_encoder", "enable_vp8_decoder",
                     "enable_vp9_encoder", "enable_vp9_decoder",
                     "enable_h264_encoder", "enable_h264_decoder",
                     "enable_h265_encoder", "enable_h265_decoder"
                 })
        {
            root.GetProperty(codecKey).GetBoolean().ShouldBeTrue(codecKey);
        }
    }

    [Fact]
    public async Task GetCallConfig_ReflectsConfiguredRuntimeValues()
    {
        var options = DefaultOptions();
        options.Calls.RuntimeConfig.UseSystemAec = false;
        options.Calls.RuntimeConfig.EnableStunMarking = true;
        options.Calls.RuntimeConfig.EnableH265Encoder = false;
        options.Calls.RuntimeConfig.HangupUiTimeout = 12.5;

        var dataJson = await InvokeAsync(CreateHandler(options), AuthorizedUserId);

        using var doc = JsonDocument.Parse(((TDataJSON)dataJson).Data);
        var root = doc.RootElement;

        root.GetProperty("use_system_aec").GetBoolean().ShouldBeFalse();
        root.GetProperty("voip_enable_stun_marking").GetBoolean().ShouldBeTrue();
        root.GetProperty("enable_h265_encoder").GetBoolean().ShouldBeFalse();
        root.GetProperty("hangup_ui_timeout").GetDouble().ShouldBe(12.5);
        // Untouched knobs keep their defaults.
        root.GetProperty("enable_vp8_encoder").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetCallConfig_IsConsistentAcrossCalls()
    {
        var handler = CreateHandler(DefaultOptions());

        // R1.2: for the same configuration version the returned config is identical across calls.
        var first = ((TDataJSON)await InvokeAsync(handler, AuthorizedUserId)).Data;
        var second = ((TDataJSON)await InvokeAsync(handler, AuthorizedUserId)).Data;

        second.ShouldBe(first);
    }

    [Fact]
    public async Task GetCallConfig_UnauthorizedSession_ThrowsAuthKeyUnregistered()
    {
        var handler = CreateHandler(DefaultOptions());

        // R1.3: a session whose auth key is not bound to a user (UserId == 0) is rejected.
        var ex = await Should.ThrowAsync<MyTelegram.RpcException>(() => InvokeAsync(handler, userId: 0));
        ex.Message.ShouldBe("AUTH_KEY_UNREGISTERED");
    }

    [Fact]
    public async Task GetCallConfig_NoReflectorsConfigured_StillReturnsConfig()
    {
        // This method carries tgcalls runtime knobs, not media endpoints (those are handed out as
        // phoneConnectionWebrtc by phone.confirmCall), so an empty reflector list is irrelevant here.
        // It must not fail: TDLib issues phone.getCallConfig from CallActor::start_up and discards the
        // call outright if it errors.
        var handler = CreateHandler(OptionsWith(new List<WebRtcConnection>()));

        var dataJson = await InvokeAsync(handler, AuthorizedUserId);

        using var doc = JsonDocument.Parse(((TDataJSON)dataJson).Data);
        doc.RootElement.GetProperty("enable_vp8_encoder").GetBoolean().ShouldBeTrue();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static MyTelegramMessengerServerOptions DefaultOptions()
        => OptionsWith(new List<WebRtcConnection>
        {
            new() { Ip = "10.0.0.1", Port = 3478, Stun = true, Turn = true, UserName = "u", Password = "p" }
        });

    private static MyTelegramMessengerServerOptions OptionsWith(List<WebRtcConnection> webRtcConnections)
        => new() { WebRtcConnections = webRtcConnections };

    private static object CreateHandler(MyTelegramMessengerServerOptions options)
    {
        var assembly = typeof(GroupCallDocument).Assembly;
        var type = assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Phone.GetCallConfigHandler",
            throwOnError: true)!;
        var monitor = new StaticOptionsMonitor<MyTelegramMessengerServerOptions>(options);
        return Activator.CreateInstance(type, monitor)!;
    }

    private static async Task<IDataJSON> InvokeAsync(object handler, long userId)
    {
        var method = handler.GetType().GetMethod("HandleAsync", new[] { typeof(IRequestInput), typeof(IObject) })!;
        var input = PhoneTestFixtures.RequestInput(userId).WithUserId(userId).Build();
        var request = new RequestGetCallConfig();

        object taskObj;
        try
        {
            taskObj = method.Invoke(handler, new object[] { input, request })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var result = await (Task<IObject>)taskObj;
        var rpcResult = (TRpcResult)result;
        return (IDataJSON)rpcResult.Result;
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> returning a fixed value (a single config version).</summary>
file sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
