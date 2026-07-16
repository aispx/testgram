using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.Services.Stats;

namespace MyTelegram.Messenger.Tests.Stats;

/// <summary>
/// Unit + property checks for <see cref="PublicForwardIngestionSubscriber.ComputeOrderKey"/>, the
/// deterministic total-ordering key the public-forward ingestion subscriber assigns to each recorded
/// forward. A stable, date-then-message-id ordering underpins stable pagination (Requirement 11.4).
/// </summary>
public class PublicForwardOrderKeyTests
{
    [Fact]
    public void ComputeOrderKey_is_deterministic_for_the_same_inputs()
    {
        var first = PublicForwardIngestionSubscriber.ComputeOrderKey(1_700_000_000, 42);
        var second = PublicForwardIngestionSubscriber.ComputeOrderKey(1_700_000_000, 42);

        second.ShouldBe(first);
    }

    [Fact]
    public void ComputeOrderKey_orders_by_date_first()
    {
        // A later date always sorts after an earlier date, regardless of message id.
        var earlierDateLargerMsg = PublicForwardIngestionSubscriber.ComputeOrderKey(1_700_000_000, int.MaxValue);
        var laterDateSmallerMsg = PublicForwardIngestionSubscriber.ComputeOrderKey(1_700_000_001, 1);

        laterDateSmallerMsg.ShouldBeGreaterThan(earlierDateLargerMsg);
    }

    [Fact]
    public void ComputeOrderKey_breaks_ties_on_message_id()
    {
        const int date = 1_700_000_000;
        var smaller = PublicForwardIngestionSubscriber.ComputeOrderKey(date, 10);
        var larger = PublicForwardIngestionSubscriber.ComputeOrderKey(date, 11);

        larger.ShouldBeGreaterThan(smaller);
    }

    /// <summary>
    /// For any two forwards with non-negative dates and message ids, the ordering key is strictly
    /// monotonic in <c>(date, messageId)</c> lexicographic order.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ComputeOrderKey_is_monotonic_in_date_then_message_id()
    {
        var gen =
            from d1 in Gen.Choose(0, int.MaxValue)
            from d2 in Gen.Choose(0, int.MaxValue)
            from m1 in Gen.Choose(0, int.MaxValue)
            from m2 in Gen.Choose(0, int.MaxValue)
            select (d1, d2, m1, m2);

        return Prop.ForAll(Arb.From(gen), t =>
        {
            var (d1, d2, m1, m2) = t;
            var k1 = PublicForwardIngestionSubscriber.ComputeOrderKey(d1, m1);
            var k2 = PublicForwardIngestionSubscriber.ComputeOrderKey(d2, m2);

            var lexicographicallyLess = d1 < d2 || (d1 == d2 && m1 < m2);
            var lexicographicallyEqual = d1 == d2 && m1 == m2;

            if (lexicographicallyEqual)
            {
                return k1 == k2;
            }

            return lexicographicallyLess ? k1 < k2 : k1 > k2;
        });
    }
}
