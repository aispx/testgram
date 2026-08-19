using MyTelegram.Messenger.Services.Scheduled;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Feature: scheduled messages — the cache hash of messages.getScheduledHistory.
///
/// <para>
/// "To generate the hash, populate the ids array with the id, edit_date (0 if unedited) and date (in
/// this order) of the previously returned messages": an unchanged queue must answer
/// <c>messages.messagesNotModified</c>, and any edit or reschedule must break the hash.
/// See https://corefork.telegram.org/api/offsets#hash-generation
/// </para>
/// </summary>
public class ScheduledMessagesHashTests
{
    [Fact]
    public void The_hash_follows_the_documented_id_editdate_date_order()
    {
        var documents = new[]
        {
            ScheduledMessageStoreTests.Document(scheduledMessageId: 7, scheduleDate: 1_700_000_100),
            ScheduledMessageStoreTests.Document(scheduledMessageId: 9, scheduleDate: 1_700_000_200)
        };
        documents[1].EditDate = 1_699_999_999;

        var expected = 0L;
        foreach (var document in documents)
        {
            expected = Fold(expected, document.ScheduledMessageId);
            expected = Fold(expected, document.EditDate ?? 0);
            expected = Fold(expected, document.ScheduleDate);
        }

        ScheduledMessagesResponseBuilder.CalcHash(documents).ShouldBe(expected);
    }

    [Fact]
    public void Editing_or_rescheduling_a_message_changes_the_hash()
    {
        var documents = new[]
        {
            ScheduledMessageStoreTests.Document(scheduledMessageId: 7, scheduleDate: 1_700_000_100)
        };
        var original = ScheduledMessagesResponseBuilder.CalcHash(documents);

        documents[0].EditDate = 1_700_000_050;
        ScheduledMessagesResponseBuilder.CalcHash(documents).ShouldNotBe(original);

        documents[0].EditDate = null;
        documents[0].ScheduleDate = 1_700_000_999;
        ScheduledMessagesResponseBuilder.CalcHash(documents).ShouldNotBe(original);
    }

    private static long Fold(long hash, long id)
    {
        hash ^= hash >> 21;
        hash ^= hash << 35;
        hash ^= hash >> 4;
        return hash + id;
    }
}
