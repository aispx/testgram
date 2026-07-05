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
///   * 1.1 - a <c>dataJSON</c> object carrying the tgcalls call-configuration parameters is returned.
///   * 1.2 - the configuration is consistent: the same server options always yield the same config,
///           and the returned ICE servers reflect the configured WebRTC reflectors.
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

        // R1.1: the tgcalls config parameters the client library expects are present.
        root.TryGetProperty("iceServers", out var iceServers).ShouldBeTrue();
        iceServers.ValueKind.ShouldBe(JsonValueKind.Array);
        iceServers.GetArrayLength().ShouldBeGreaterThan(0);

        root.GetProperty("defaultProtocol").GetString().ShouldBe("udp");
        root.GetProperty("udpP2P").GetBoolean().ShouldBeTrue();
        root.GetProperty("udpReflector").GetBoolean().ShouldBeTrue();
        root.GetProperty("minLayer").GetInt32().ShouldBe(65);
        root.GetProperty("maxLayer").GetInt32().ShouldBe(92);
    }

    [Fact]
    public async Task GetCallConfig_IceServersReflectConfiguredReflectors()
    {
        // A STUN-only reflector and a TURN reflector with credentials.
        var options = OptionsWith(new List<WebRtcConnection>
        {
            new() { Ip = "1.2.3.4", Port = 3478, Stun = true },
            new() { Ip = "5.6.7.8", Port = 5349, Turn = true, UserName = "turnuser", Password = "turnpass" }
        });
        var handler = CreateHandler(options);

        var dataJson = await InvokeAsync(handler, AuthorizedUserId);
        using var doc = JsonDocument.Parse(((TDataJSON)dataJson).Data);
        var iceServers = doc.RootElement.GetProperty("iceServers");

        // R1.2: the STUN reflector is surfaced as a stun: url.
        var stunUrls = AllUrls(iceServers);
        stunUrls.ShouldContain("stun:1.2.3.4:3478");

        // R1.2: the TURN reflector is surfaced as udp + tcp turn: urls with its credentials.
        stunUrls.ShouldContain("turn:5.6.7.8:5349?transport=udp");
        stunUrls.ShouldContain("turn:5.6.7.8:5349?transport=tcp");

        var turnServer = EnumerateServers(iceServers)
            .Single(s => s.GetProperty("urls").EnumerateArray().Any(u => u.GetString()!.StartsWith("turn:")));
        turnServer.GetProperty("username").GetString().ShouldBe("turnuser");
        turnServer.GetProperty("credential").GetString().ShouldBe("turnpass");
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
    public async Task GetCallConfig_NoReflectorsConfigured_ThrowsConfigurationError()
    {
        // With no WebRTC reflectors configured the handler surfaces the deployment error clearly
        // rather than returning an empty iceServers list.
        var handler = CreateHandler(OptionsWith(new List<WebRtcConnection>()));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => InvokeAsync(handler, AuthorizedUserId));
        ex.Message.ShouldContain("WebRtcConnections");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static IReadOnlyList<string> AllUrls(JsonElement iceServers)
        => EnumerateServers(iceServers)
            .SelectMany(s => s.GetProperty("urls").EnumerateArray().Select(u => u.GetString()!))
            .ToList();

    private static IEnumerable<JsonElement> EnumerateServers(JsonElement iceServers)
        => iceServers.EnumerateArray();

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
