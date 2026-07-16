using FsCheck;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// FsCheck <see cref="Arbitrary{T}"/> registration surface for the stats-api property tests. Reference it
/// from a test with <c>[Properties(Arbitrary = new[] { typeof(StatsArbitraries) })]</c> (class level) or
/// <c>[Property(Arbitrary = new[] { typeof(StatsArbitraries) })]</c> (method level) and FsCheck resolves
/// generators for the custom fixture types below automatically.
///
/// Feature: stats-api. Backs the property tasks in later waves (Properties 1-17); each property test runs
/// a minimum of 100 generated cases via <c>[Property(MaxTest = 100, ...)]</c>.
/// </summary>
public static class StatsArbitraries
{
    public static Arbitrary<StatsChannelFixture> BroadcastChannel() => Arb.From(StatsGen.BroadcastChannel);

    public static Arbitrary<StatsAccessCaseFixture> AccessCase() => Arb.From(StatsGen.AccessCase);

    public static Arbitrary<DailyMetricSeriesFixture> MetricSeries() => Arb.From(StatsGen.MetricSeries);

    public static Arbitrary<GraphSpecFixture> GraphSpec() => Arb.From(StatsGen.GraphSpec);

    public static Arbitrary<ForwardEventSequenceFixture> ForwardEventSequence() =>
        Arb.From(StatsGen.ForwardEventSequence);

    public static Arbitrary<AsyncTokenFixture> AsyncToken() => Arb.From(StatsGen.AsyncToken);
}
