using MyTelegram.Core;

namespace MyTelegram.MTProto.Tests;

/// <summary>
///     <see cref="MessageIdHelper" /> is a singleton shared by every connection.
///     https://corefork.telegram.org/mtproto/description#message-identifier-msg-id
/// </summary>
public class MessageIdHelperTest
{
    [Fact]
    public void GenerateMessageId_IsStrictlyIncreasingAndAlwaysOneMod4()
    {
        var helper = new MessageIdHelper();

        var previous = 0L;
        for (var i = 0; i < 10_000; i++)
        {
            var id = helper.GenerateMessageId();

            (id % 4).ShouldBe(1, "a server response to a client query must be 1 mod 4");
            id.ShouldBeGreaterThan(previous);
            previous = id;
        }
    }

    [Fact]
    public void GenerateMessageId_UnderConcurrency_NeverHandsOutTheSameIdTwice()
    {
        // The unsynchronised read-modify-write of _lastMessageId let two concurrent handshakes receive the
        // same msg_id, which a client is entitled to treat as a duplicate and drop.
        const int threads = 8;
        const int perThread = 5_000;

        var helper = new MessageIdHelper();
        var results = new long[threads][];

        Parallel.For(0, threads, t =>
        {
            var ids = new long[perThread];
            for (var i = 0; i < perThread; i++)
            {
                ids[i] = helper.GenerateMessageId();
            }

            results[t] = ids;
        });

        var all = results.SelectMany(x => x).ToArray();

        all.Length.ShouldBe(threads * perThread);
        all.Distinct().Count().ShouldBe(all.Length, "msg_id must be unique across concurrent callers");
        all.ShouldAllBe(id => id % 4 == 1);
    }

    [Fact]
    public void GenerateMessageId_CarriesTheUnixTimeInTheUpper32Bits()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = new MessageIdHelper().GenerateMessageId();
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        (id >> 32).ShouldBeInRange(before, after);
    }

    /// <summary>
    ///     A counter that is only just ahead of the clock is an ordinary tie — two calls in the same
    ///     millisecond — and must still be resolved by stepping forward, not by reusing the clock value.
    /// </summary>
    [Fact]
    public void GenerateMessageId_ANearFutureCounterStillStepsForward()
    {
        var helper = new MessageIdHelper();
        var justAhead = ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5) << 32) | 1;
        SetLastMessageId(helper, justAhead);

        helper.GenerateMessageId().ShouldBe(justAhead + 4);
    }

    /// <summary>
    ///     A counter far ahead of the clock is corrupt, not a tie: because every id is otherwise derived as
    ///     `last + 4`, one bad value freezes the time carried by every subsequent msg_id for as long as the
    ///     process lives. Clients reject a msg_id whose time is far from their own — Telethon logs "Server
    ///     sent a very new message" and discards it — so the connection completes and then goes deaf.
    /// </summary>
    [Fact]
    public void GenerateMessageId_RecoversFromACounterFarAheadOfTheClock()
    {
        var helper = new MessageIdHelper();
        var twoYearsAhead = ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 743 * 86400) << 32) | 1;
        SetLastMessageId(helper, twoYearsAhead);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var id = helper.GenerateMessageId();
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        (id >> 32).ShouldBeInRange(before, after, "the clock has to win over a corrupt counter");
        (id % 4).ShouldBe(1);

        // And it keeps working from there, rather than snapping back to the bogus value.
        helper.GenerateMessageId().ShouldBeGreaterThan(id);
    }

    private static void SetLastMessageId(MessageIdHelper helper, long value)
    {
        var field = typeof(MessageIdHelper).GetField("_lastMessageId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        field.ShouldNotBeNull();
        field!.SetValue(helper, value);
    }
}
