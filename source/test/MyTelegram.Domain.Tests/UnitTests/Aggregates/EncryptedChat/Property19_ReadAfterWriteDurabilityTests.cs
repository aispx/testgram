using EventFlow.Aggregates;
using EventFlow.ReadStores;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.ReadModel.Impl;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 19: Read-after-write and read-model durability.
///
/// For any command mutating a secret chat, the read model observed after the command reflects the
/// ChatState and every field produced by the events; and after rebuilding state from the previously
/// stored events (a restart), the fields (admin_id, participant_id, Authorization_Key ids, access_hash,
/// g_a, g_b, key_fingerprint, random_id) are identical to what was stored before the restart.
///
/// Validates: Requirements 17.1, 17.6.
///
/// The test drives the real aggregate to produce real domain events, replays those same events into a
/// fresh aggregate (the restart path EventFlow uses) and into the real read-model projection, then
/// compares all three views field by field. Each run executes at least 100 generated cases.
/// </summary>
public class Property19_ReadAfterWriteDurabilityTests
{
    private static readonly IReadModelContext Context = new Mock<IReadModelContext>(MockBehavior.Loose).Object;

    public sealed record ChatCase(
        int ChatId,
        long AdminId,
        long ParticipantId,
        long AdminPermAuthKeyId,
        long ParticipantPermAuthKeyId,
        long AccessHash,
        byte[] Ga,
        byte[] Gb,
        long KeyFingerprint,
        int RandomId,
        int Date,
        bool Accept,
        bool Discard,
        bool DeleteHistory);

    [Property(Arbitrary = new[] { typeof(ChatCaseArbitraries) }, MaxTest = 100)]
    public void Read_model_and_replayed_state_match_the_written_events(ChatCase testCase)
    {
        var aggregateId = EncryptedChatId.Create(testCase.ChatId);
        var aggregate = new EncryptedChatAggregate(aggregateId);

        aggregate.CreateEncryptedChat(testCase.ChatId, testCase.AdminId, testCase.ParticipantId,
            testCase.AdminPermAuthKeyId, testCase.AccessHash, testCase.Ga, testCase.RandomId, testCase.Date);

        if (testCase.Accept)
        {
            aggregate.AcceptEncryptedChat(testCase.ParticipantId, testCase.ParticipantPermAuthKeyId, testCase.Gb,
                testCase.KeyFingerprint, testCase.Date + 1);
        }

        if (testCase.Discard)
        {
            aggregate.DiscardEncryptedChat(testCase.AdminId, testCase.DeleteHistory, testCase.Date + 2);
        }

        // The events the aggregate produced — this is exactly what the event store persists.
        var storedEvents = aggregate.UncommittedEvents
            .Select((e, index) => BuildDomainEvent(aggregateId, e.AggregateEvent, index + 1))
            .ToList();

        // 1. Read-after-write: project the stored events into the read model.
        var readModel = new EncryptedChatReadModel();
        foreach (var domainEvent in storedEvents)
        {
            Project(readModel, domainEvent);
        }

        // 2. Durability: rebuild aggregate state from the same stored events (restart path).
        var rebuilt = new EncryptedChatAggregate(aggregateId);
        rebuilt.ApplyEvents(storedEvents);
        var rebuiltState = EncryptedChatTestHelper.GetState(rebuilt);
        var originalState = EncryptedChatTestHelper.GetState(aggregate);

        var expectedState = testCase.Discard
            ? ChatState.Discarded
            : testCase.Accept
                ? ChatState.Active
                : ChatState.Waiting;

        // Read model reflects the written events.
        readModel.Id.ShouldBe(aggregateId.Value);
        readModel.ChatId.ShouldBe(testCase.ChatId);
        readModel.AdminId.ShouldBe(testCase.AdminId);
        readModel.ParticipantId.ShouldBe(testCase.ParticipantId);
        readModel.AdminPermAuthKeyId.ShouldBe(testCase.AdminPermAuthKeyId);
        readModel.AccessHash.ShouldBe(testCase.AccessHash);
        readModel.Ga.ShouldBe(testCase.Ga);
        readModel.RandomId.ShouldBe(testCase.RandomId);
        readModel.Date.ShouldBe(testCase.Date);
        readModel.ChatState.ShouldBe(expectedState);

        if (testCase.Accept)
        {
            readModel.Gb.ShouldBe(testCase.Gb);
            readModel.KeyFingerprint.ShouldBe(testCase.KeyFingerprint);
            readModel.ParticipantPermAuthKeyId.ShouldBe(testCase.ParticipantPermAuthKeyId);
        }

        if (testCase.Discard)
        {
            readModel.HistoryDeleted.ShouldBe(testCase.DeleteHistory);
        }

        // Rebuilt state is identical to the pre-restart state, field by field.
        rebuiltState.ChatId.ShouldBe(originalState.ChatId);
        rebuiltState.AdminId.ShouldBe(originalState.AdminId);
        rebuiltState.ParticipantId.ShouldBe(originalState.ParticipantId);
        rebuiltState.AdminPermAuthKeyId.ShouldBe(originalState.AdminPermAuthKeyId);
        rebuiltState.ParticipantPermAuthKeyId.ShouldBe(originalState.ParticipantPermAuthKeyId);
        rebuiltState.AccessHash.ShouldBe(originalState.AccessHash);
        rebuiltState.Ga.ShouldBe(originalState.Ga);
        rebuiltState.Gb.ShouldBe(originalState.Gb);
        rebuiltState.KeyFingerprint.ShouldBe(originalState.KeyFingerprint);
        rebuiltState.RandomId.ShouldBe(originalState.RandomId);
        rebuiltState.Date.ShouldBe(originalState.Date);
        rebuiltState.State.ShouldBe(expectedState);
        rebuiltState.HistoryDeleted.ShouldBe(originalState.HistoryDeleted);
    }

    private static IDomainEvent BuildDomainEvent(EncryptedChatId aggregateId, IAggregateEvent aggregateEvent,
        int sequence)
    {
        return aggregateEvent switch
        {
            EncryptedChatCreatedEvent e =>
                new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatCreatedEvent>(e,
                    Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, sequence),
            EncryptedChatAcceptedEvent e =>
                new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatAcceptedEvent>(e,
                    Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, sequence),
            EncryptedChatDiscardedEvent e =>
                new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatDiscardedEvent>(e,
                    Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, sequence),
            EncryptedChatSpamReportedEvent e =>
                new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatSpamReportedEvent>(e,
                    Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, sequence),
            _ => throw new NotSupportedException(aggregateEvent.GetType().Name)
        };
    }

    private static void Project(EncryptedChatReadModel readModel, IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatCreatedEvent> e1:
                readModel.ApplyAsync(Context, e1, CancellationToken.None).GetAwaiter().GetResult();
                break;
            case IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatAcceptedEvent> e2:
                readModel.ApplyAsync(Context, e2, CancellationToken.None).GetAwaiter().GetResult();
                break;
            case IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatDiscardedEvent> e3:
                readModel.ApplyAsync(Context, e3, CancellationToken.None).GetAwaiter().GetResult();
                break;
            case IDomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatSpamReportedEvent> e4:
                readModel.ApplyAsync(Context, e4, CancellationToken.None).GetAwaiter().GetResult();
                break;
        }
    }

    public static class ChatCaseArbitraries
    {
        public static Arbitrary<ChatCase> ChatCase()
        {
            var idGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
            var blobGen = Gen.Choose(0, 300).SelectMany(size => Gen.ArrayOf(size, Arb.Generate<byte>()));

            var gen =
                from chatId in Gen.Choose(1, 1_000_000)
                from adminId in idGen
                from participantDelta in Gen.Choose(1, 1000).Select(i => (long)i)
                from adminKey in idGen
                from participantKey in idGen
                from accessHash in Arb.Generate<long>()
                from ga in blobGen
                from gb in blobGen
                from keyFingerprint in Arb.Generate<long>()
                from randomId in Arb.Generate<int>()
                from date in Gen.Choose(1, int.MaxValue)
                from accept in Arb.Generate<bool>()
                from discard in Arb.Generate<bool>()
                from deleteHistory in Arb.Generate<bool>()
                select new ChatCase(chatId, adminId, adminId + participantDelta, adminKey, participantKey,
                    accessHash, ga, gb, keyFingerprint, randomId, date, accept, discard, deleteHistory);

            return Arb.From(gen);
        }
    }
}
