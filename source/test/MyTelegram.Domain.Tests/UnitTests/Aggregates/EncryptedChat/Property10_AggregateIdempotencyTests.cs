using MyTelegram.Domain.Aggregates.EncryptedChat;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 10 (aggregate portion): Idempotency by dedup key.
///
/// Repeated reportEncryptedSpam by the same caller produces at most one spam record and one event;
/// a repeated CreateEncryptedChat on an already-created aggregate is rejected (AggregateIsNew), so a
/// retried requestEncryption cannot create a second chat for the same EncryptedChatId.
///
/// Validates: Requirements 3.8, 14.4 (the message-blob dedup portion is covered by the store tests).
/// </summary>
public class Property10_AggregateIdempotencyTests
{
    private const long AdminId = 1001;
    private const long ParticipantId = 2002;

    private static EncryptedChatAggregate CreatedChat()
    {
        var aggregate = EncryptedChatTestHelper.NewAggregate(3);
        aggregate.CreateEncryptedChat(3, AdminId, ParticipantId, adminPermAuthKeyId: 10,
            accessHash: 55, ga: [1], randomId: 7, date: 100);

        return aggregate;
    }

    [Fact]
    public void Repeated_report_spam_from_same_caller_emits_at_most_one_event()
    {
        var aggregate = CreatedChat();

        aggregate.ReportEncryptedChatSpam(ParticipantId);
        aggregate.ReportEncryptedChatSpam(ParticipantId);
        aggregate.ReportEncryptedChatSpam(ParticipantId);

        aggregate.UncommittedEvents.Count(e => e.AggregateEvent is EncryptedChatSpamReportedEvent)
            .ShouldBe(1);
        EncryptedChatTestHelper.GetState(aggregate).SpamReporters.Count.ShouldBe(1);
    }

    [Fact]
    public void Distinct_callers_each_record_one_spam_report()
    {
        var aggregate = CreatedChat();

        aggregate.ReportEncryptedChatSpam(AdminId);
        aggregate.ReportEncryptedChatSpam(ParticipantId);
        aggregate.ReportEncryptedChatSpam(AdminId);

        aggregate.UncommittedEvents.Count(e => e.AggregateEvent is EncryptedChatSpamReportedEvent)
            .ShouldBe(2);
        EncryptedChatTestHelper.GetState(aggregate).SpamReporters.Count.ShouldBe(2);
    }

    [Fact]
    public void Recreating_an_existing_chat_is_rejected()
    {
        var aggregate = CreatedChat();

        Should.Throw<Exception>(() =>
            aggregate.CreateEncryptedChat(3, AdminId, ParticipantId, adminPermAuthKeyId: 10,
                accessHash: 55, ga: [1], randomId: 7, date: 100));
    }
}
