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
}
