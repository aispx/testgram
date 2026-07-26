using EventFlow.Aggregates;
using EventFlow.ReadStores;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram.Domain.Aggregates.EncryptedChat;
using MyTelegram.ReadModel.Impl;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 1: Blind-relay byte/bit fidelity.
///
/// For any secret chat and any values of g_a, g_b, key_fingerprint and message blob, the value read
/// back from storage and relayed to the other participant is byte-identical (key_fingerprint bit-identical
/// over the full 64-bit range) to the value received from the sender; the server never decodes, parses or
/// branches on their contents.
///
/// Validates: Requirements 3.6, 6.5, 8.6, 11.2, 16.1, 16.3, 16.4, 16.5, 16.7, 17.5.
/// </summary>
public class Property01_BlindRelayFidelityTests
{
    private const long AdminId = 1001;
    private const long ParticipantId = 2002;

    private static readonly IReadModelContext Context = new Mock<IReadModelContext>(MockBehavior.Loose).Object;

    [Property(Arbitrary = new[] { typeof(BlobArbitraries) }, MaxTest = 200)]
    public void Ga_Gb_and_key_fingerprint_round_trip_byte_identically(byte[] ga, byte[] gb, long keyFingerprint)
    {
        var readModel = ProjectEstablishedChat(ga, gb, keyFingerprint);

        var converter = new EncryptedChatConverter();

        // The participant (admin's counterpart) sees g_a; a fresh requested view carries g_a verbatim.
        var requested = (TEncryptedChatRequested)converter.ToEncryptedChatRequested(readModel);
        requested.GA.ShouldBe(ga);

        // The admin sees g_b in the established chat.
        var chatForAdmin = (TEncryptedChat)converter.ToEncryptedChat(readModel, AdminId);
        chatForAdmin.GAOrB.ToArray().ShouldBe(gb);
        chatForAdmin.KeyFingerprint.ShouldBe(keyFingerprint);

        // The participant sees g_a in the established chat.
        var chatForParticipant = (TEncryptedChat)converter.ToEncryptedChat(readModel, ParticipantId);
        chatForParticipant.GAOrB.ToArray().ShouldBe(ga);
        chatForParticipant.KeyFingerprint.ShouldBe(keyFingerprint);
    }

    [Property(Arbitrary = new[] { typeof(BlobArbitraries) }, MaxTest = 200)]
    public void Message_blob_round_trips_byte_identically(byte[] blob)
    {
        var messageReadModel = new FakeEncryptedMessage(blob);
        var converter = new EncryptedMessageConverter();

        var message = (TEncryptedMessage)converter.ToEncryptedMessage(messageReadModel, file: null);
        message.Bytes.ToArray().ShouldBe(blob);

        var serviceMessage = (TEncryptedMessageService)converter.ToEncryptedMessageService(messageReadModel);
        serviceMessage.Bytes.ToArray().ShouldBe(blob);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Key_fingerprint_boundary_values_round_trip_bit_identically(long keyFingerprint)
    {
        var readModel = ProjectEstablishedChat([1], [2], keyFingerprint);
        var converter = new EncryptedChatConverter();

        var chat = (TEncryptedChat)converter.ToEncryptedChat(readModel, AdminId);
        chat.KeyFingerprint.ShouldBe(keyFingerprint);
    }

    private static EncryptedChatReadModel ProjectEstablishedChat(byte[] ga, byte[] gb, long keyFingerprint)
    {
        var readModel = new EncryptedChatReadModel();
        var aggregateId = EncryptedChatId.Create(5);

        var created = new EncryptedChatCreatedEvent(5, AdminId, ParticipantId, adminPermAuthKeyId: 10,
            accessHash: 77, ga: ga, randomId: 42, date: 100);
        readModel.ApplyAsync(Context,
            new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatCreatedEvent>(created,
                Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 1),
            CancellationToken.None).GetAwaiter().GetResult();

        var accepted = new EncryptedChatAcceptedEvent(participantPermAuthKeyId: 20, gb: gb,
            keyFingerprint: keyFingerprint, date: 200);
        readModel.ApplyAsync(Context,
            new DomainEvent<EncryptedChatAggregate, EncryptedChatId, EncryptedChatAcceptedEvent>(accepted,
                Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 2),
            CancellationToken.None).GetAwaiter().GetResult();

        readModel.Ga.ShouldBe(ga);
        readModel.Gb.ShouldBe(gb);
        readModel.KeyFingerprint.ShouldBe(keyFingerprint);

        return readModel;
    }

    private sealed class FakeEncryptedMessage(byte[] data) : IEncryptedMessageReadModel
    {
        public long ChatId => 5;
        public long UserId => AdminId;
        public long PermAuthKeyId => 10;
        public byte[] Data => data;
        public byte[]? File => null;
        public int Date => 123;
        public string Id => "5_1001_42";
        public SendMessageType MessageType => SendMessageType.Text;
        public int Qts => 1;
        public long RandomId => 42;
    }

    public static class BlobArbitraries
    {
        // Empty, single-byte, 4KB and arbitrary byte arrays; key_fingerprint across the full long range.
        public static Arbitrary<byte[]> Blob()
        {
            var sizeGen = Gen.Frequency(
                Tuple.Create(1, Gen.Constant(0)),
                Tuple.Create(1, Gen.Constant(1)),
                Tuple.Create(1, Gen.Constant(4096)),
                Tuple.Create(3, Gen.Choose(0, 512)));

            var gen = sizeGen.SelectMany(size =>
                Gen.ArrayOf(size, Arb.Generate<byte>()));

            return Arb.From(gen);
        }
    }
}
