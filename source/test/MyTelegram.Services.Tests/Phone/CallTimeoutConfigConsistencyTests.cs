using Moq;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Core;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger;
using MyTelegram.Schema;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Guards the one invariant that ties <see cref="CallsConfig"/> to <c>help.getConfig</c>: the server's
/// own expiry deadlines must match the <c>call_*_timeout_ms</c> values it publishes to clients.
///
/// <para>The two live in different assemblies - <c>ConfigConverter</c> (MyTelegram.Converters) cannot
/// reference <see cref="CallsConfig"/> (MyTelegram.Messenger) - so the values are necessarily written
/// out twice. Nothing but this test would catch them drifting apart, and drift is not benign: if the
/// server's deadline is shorter than the client's, <see cref="MyTelegram.Messenger.Services.Phone.CallSessionExpiryService"/>
/// tears down calls that are still ringing perfectly normally on the callee's device.</para>
/// </summary>
public class CallTimeoutConfigConsistencyTests
{
    [Fact]
    public void ExpiryDeadlines_MatchTheTimeoutsPublishedInHelpGetConfig()
    {
        var calls = new CallsConfig();
        var published = BuildPublishedConfig();

        // The sweeper measures in seconds; help.getConfig publishes milliseconds.
        (calls.ReceiveTimeoutSeconds * 1000).ShouldBe(published.CallReceiveTimeoutMs);
        (calls.RingTimeoutSeconds * 1000).ShouldBe(published.CallRingTimeoutMs);
        (calls.ConnectTimeoutSeconds * 1000).ShouldBe(published.CallConnectTimeoutMs);
    }

    [Fact]
    public void GracePeriod_IsPositive_SoTheServerNeverPreEmptsTheClientsOwnTimer()
    {
        // Clients are expected to send phone.discardCall themselves off the published timeouts; the
        // sweeper is only a fallback for clients that died. Without a grace period the two race.
        new CallsConfig().ExpiryGraceSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ConnectedCallBackstop_IsFarLongerThanAnyPreConnectDeadline()
    {
        var calls = new CallsConfig();

        // A multi-hour call is legitimate: the backstop exists only so an abandoned session cannot
        // mark both participants busy forever.
        calls.MaxCallDurationSeconds.ShouldBeGreaterThan(calls.RingTimeoutSeconds * 100);
    }

    /// <summary>
    /// Builds the <c>help.getConfig</c> payload the server actually serves. <c>ConfigConverter</c> is
    /// internal, so it is constructed reflectively (the same approach the other handler tests here use);
    /// the mapper is never touched because no DC options are passed.
    /// </summary>
    private static IConfig BuildPublishedConfig()
    {
        var type = typeof(IConfigConverter).Assembly.GetType(
            "MyTelegram.Converters.TLObjects.LatestLayer.ConfigConverter",
            throwOnError: true)!;
        var converter = (IConfigConverter)Activator.CreateInstance(type, Mock.Of<IObjectMapper>())!;

        return converter.ToConfig([], thisDcId: 1, mediaDcId: 2);
    }
}
