namespace MyTelegram.Messenger.Services.Scheduled;

/// <summary>
/// Limits and special values of the schedule queue.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public static class ScheduledMessageRules
{
    /// <summary>
    /// Special <c>schedule_date</c> meaning "send as soon as the peer comes online". Only valid for
    /// private chats.
    /// </summary>
    public const int WhenOnlineDate = 0x7FFFFFFE;

    /// <summary>
    /// "If the schedule_date is less than 10 seconds in the future, the message will be sent
    /// immediately, generating a normal updateNewMessage/updateNewChannelMessage."
    /// </summary>
    public const int SendNowThresholdSeconds = 10;

    /// <summary>
    /// A message cannot be scheduled more than a year in the future.
    /// </summary>
    public const int MaxScheduleOffsetSeconds = 365 * 86400;

    /// <summary>
    /// Maximum number of messages one user may keep queued in a single peer.
    /// </summary>
    public const int MaxQueuedMessagesPerPeer = 100;

    private const int Day = 86400;

    /// <summary>
    /// Allowed <c>schedule_repeat_period</c> values: every day, week, two weeks, month, three months,
    /// twice a year, year. 60 and 300 seconds are additionally allowed on test servers.
    /// </summary>
    public static readonly IReadOnlySet<int> AllowedRepeatPeriods = new HashSet<int>
    {
        60,
        300,
        Day,
        7 * Day,
        14 * Day,
        30 * Day,
        91 * Day,
        182 * Day,
        365 * Day
    };

    public static bool IsWhenOnline(int? scheduleDate) => scheduleDate == WhenOnlineDate;

    /// <summary>
    /// True when the requested date is so close that the message must bypass the queue entirely.
    /// </summary>
    public static bool ShouldSendImmediately(int scheduleDate, int now)
    {
        return !IsWhenOnline(scheduleDate) && scheduleDate - now < SendNowThresholdSeconds;
    }

    /// <summary>
    /// Validates the plain date part of the request. The peer and account dependent checks live in
    /// <see cref="ScheduledMessageStore.ValidateAsync"/>.
    /// </summary>
    public static void ValidateDate(int scheduleDate, int now)
    {
        if (IsWhenOnline(scheduleDate))
        {
            return;
        }

        if (scheduleDate <= 0)
        {
            RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
        }

        if (scheduleDate - now > MaxScheduleOffsetSeconds)
        {
            RpcErrors.RpcErrors400.ScheduleDateTooLate.ThrowRpcError();
        }
    }

    public static void ValidateRepeatPeriod(int? repeatPeriod, int batchSize)
    {
        if (!repeatPeriod.HasValue)
        {
            return;
        }

        // "schedule_repeat_period can only be used when sending/forwarding a single message".
        if (batchSize > 1 || !AllowedRepeatPeriods.Contains(repeatPeriod.Value))
        {
            RpcErrors.RpcErrors400.ScheduleDateInvalid.ThrowRpcError();
        }
    }
}
