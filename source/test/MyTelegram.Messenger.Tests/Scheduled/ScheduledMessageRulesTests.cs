using MyTelegram.Messenger.Services.Scheduled;
using Shouldly;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Feature: scheduled messages — the limits the server enforces on <c>schedule_date</c> and
/// <c>schedule_repeat_period</c>. See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public class ScheduledMessageRulesTests
{
    private const int Now = 1_700_000_000;

    [Fact]
    public void A_date_less_than_ten_seconds_away_bypasses_the_queue()
    {
        ScheduledMessageRules.ShouldSendImmediately(Now + 9, Now).ShouldBeTrue();
        ScheduledMessageRules.ShouldSendImmediately(Now + 10, Now).ShouldBeFalse();
        ScheduledMessageRules.ShouldSendImmediately(Now - 3600, Now).ShouldBeTrue();
    }

    [Fact]
    public void The_when_online_date_never_bypasses_the_queue()
    {
        // 0x7FFFFFFE is a marker, not a point in time: it waits for the peer, not for the clock.
        ScheduledMessageRules.ShouldSendImmediately(ScheduledMessageRules.WhenOnlineDate, Now).ShouldBeFalse();
        ScheduledMessageRules.IsWhenOnline(ScheduledMessageRules.WhenOnlineDate).ShouldBeTrue();
        Should.NotThrow(() => ScheduledMessageRules.ValidateDate(ScheduledMessageRules.WhenOnlineDate, Now));
    }

    [Fact]
    public void A_date_more_than_a_year_away_is_rejected()
    {
        var error = Should.Throw<RpcException>(() =>
            ScheduledMessageRules.ValidateDate(Now + ScheduledMessageRules.MaxScheduleOffsetSeconds + 1, Now));
        error.RpcError.Message.ShouldBe("SCHEDULE_DATE_TOO_LATE");

        Should.NotThrow(() =>
            ScheduledMessageRules.ValidateDate(Now + ScheduledMessageRules.MaxScheduleOffsetSeconds, Now));
    }

    [Fact]
    public void A_non_positive_date_is_rejected()
    {
        Should.Throw<RpcException>(() => ScheduledMessageRules.ValidateDate(0, Now))
            .RpcError.Message.ShouldBe("SCHEDULE_DATE_INVALID");
    }

    [Theory]
    [InlineData(86400)]
    [InlineData(7 * 86400)]
    [InlineData(14 * 86400)]
    [InlineData(30 * 86400)]
    [InlineData(91 * 86400)]
    [InlineData(182 * 86400)]
    [InlineData(365 * 86400)]
    public void The_documented_repeat_periods_are_accepted(int repeatPeriod)
    {
        Should.NotThrow(() => ScheduledMessageRules.ValidateRepeatPeriod(repeatPeriod, 1));
    }

    [Fact]
    public void An_arbitrary_repeat_period_is_rejected()
    {
        Should.Throw<RpcException>(() => ScheduledMessageRules.ValidateRepeatPeriod(2 * 86400, 1))
            .RpcError.Message.ShouldBe("SCHEDULE_DATE_INVALID");
    }

    [Fact]
    public void A_repeat_period_cannot_be_used_for_an_album()
    {
        // "schedule_repeat_period can only be used when sending/forwarding a single message".
        Should.Throw<RpcException>(() => ScheduledMessageRules.ValidateRepeatPeriod(86400, 2));
        Should.NotThrow(() => ScheduledMessageRules.ValidateRepeatPeriod(null, 5));
    }
}
