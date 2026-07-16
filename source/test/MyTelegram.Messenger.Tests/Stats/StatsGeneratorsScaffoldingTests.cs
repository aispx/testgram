using FsCheck;
using FsCheck.Xunit;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Scaffolding sanity checks for the stats-api shared generators (Feature: stats-api). These are NOT one
/// of the numbered correctness properties (1-20); they exist only to prove the test area is wired up
/// (xUnit + FsCheck resolve, arbitraries register) and that each shared generator emits fixtures whose
/// structural invariants hold — the invariants that later property tasks rely on. Each check runs the
/// standard minimum of 100 generated cases.
/// </summary>
[Properties(Arbitrary = new[] { typeof(StatsArbitraries) }, MaxTest = 100)]
public class StatsGeneratorsScaffoldingTests
{
    [Property]
    public void Broadcast_channel_fixture_is_a_broadcast(StatsChannelFixture channel)
    {
        channel.IsBroadcast.ShouldBeTrue();
        channel.IsPublic.ShouldBe(!string.IsNullOrEmpty(channel.UserName));
        channel.ParticipantsCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Property]
    public void Access_case_channel_presence_matches_resolution(StatsAccessCaseFixture accessCase)
    {
        // When the target resolves the channel is present; otherwise it is null.
        (accessCase.Channel is not null).ShouldBe(accessCase.TargetResolves);
    }

    [Property]
    public void Metric_series_days_are_unique_and_ascending(DailyMetricSeriesFixture series)
    {
        series.ReportingWindowDays.ShouldBeInRange(1, 365);

        var days = series.Points.Select(p => p.UtcDay).ToList();
        days.ShouldBe(days.OrderBy(d => d).ToList());
        days.Distinct().Count().ShouldBe(days.Count);

        foreach (var point in series.Points)
        {
            // Day keys are aligned to 00:00:00 UTC (multiples of 86,400 seconds).
            (point.UtcDay % 86_400).ShouldBe(0);
            point.Value.ShouldBeGreaterThanOrEqualTo(0);
        }
    }

    [Property]
    public void Graph_spec_x_axis_is_strictly_ascending_and_series_align(GraphSpecFixture spec)
    {
        var x = spec.XAxisMillis;
        for (var i = 1; i < x.Count; i++)
        {
            x[i].ShouldBeGreaterThan(x[i - 1]);
        }

        // Every data series has exactly one value per x-axis point.
        foreach (var s in spec.Series)
        {
            s.Values.Count.ShouldBe(x.Count);
        }

        spec.Series.Count.ShouldBeGreaterThanOrEqualTo(1);

        // A zoom spec, when present, is itself non-empty.
        if (spec.Zoom is not null)
        {
            spec.Zoom.XAxisMillis.Count.ShouldBeGreaterThan(0);
        }
    }

    [Property]
    public void Forward_event_sequence_is_consistent(ForwardEventSequenceFixture seq)
    {
        seq.Limit.ShouldBeInRange(0, 120);

        foreach (var e in seq.Events)
        {
            // All events target the sequence's single source.
            e.SourceType.ShouldBe(seq.SourceType);
            e.SourceOwnerPeerId.ShouldBe(seq.SourceOwnerPeerId);
            e.SourceItemId.ShouldBe(seq.SourceItemId);
        }
    }

    [Property]
    public void Async_token_expiry_flag_matches_age(AsyncTokenFixture token)
    {
        var age = token.NowUnix - token.IssuedAt;
        if (token.IsExpired)
        {
            age.ShouldBeGreaterThan(AsyncTokenFixture.ValidityWindowSeconds);
        }
        else
        {
            age.ShouldBeInRange(0, AsyncTokenFixture.ValidityWindowSeconds);
        }

        token.Token.ShouldNotBeNullOrEmpty();
    }
}
