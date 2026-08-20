using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using MyTelegram;
using MyTelegram.Domain.Shared;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Scheduled;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Feature: scheduled messages — the text is never stored in clear while it waits in the queue, and a
/// broadcast channel's shared queue notifies every admin allowed to post, not only the author.
/// See https://corefork.telegram.org/api/scheduled-messages
/// </summary>
public class ScheduledMessageEncryptionAndAudienceTests
{
    private const long UserId = 2010001;
    private const long PeerUserId = 2010002;
    private const long ChannelId = 1000500;

    static ScheduledMessageEncryptionAndAudienceTests() => ScheduledTestSerializers.EnsureRegistered();

    [RequiresMongoDbFact]
    public async Task Encryption_on_stores_ciphertext_and_the_flush_decrypts_it_back()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");
        var encryption = new FakeEncryptionHelper();
        var store = CreateStore(mongo.Database, encryption);

        // MessageAppService fills EncryptedData when encryption is on; the plaintext still rides on the
        // item at that point, exactly as the real send path produces it.
        var item = ScheduledMessageStoreTests.Item() with
        {
            Message = "top secret",
            EncryptedData = encryption.Encrypt(UserId, "top secret")
        };
        item = item with { ScheduleDate = 1_700_000_000 };

        await store.SaveAsync([new ScheduledQueueItem(item, null, null)], RequestInfo.Empty with { Layer = 222 });

        // On disk: no plaintext, only ciphertext.
        var stored = await collection.Find(FilterDefinition<ScheduledMessageDocument>.Empty).SingleAsync();
        stored.Item.Message.ShouldBeEmpty();
        stored.Item.EncryptedData!.Value.Length.ShouldBeGreaterThan(0);

        // Flushing rebuilds the plaintext for the send pipeline.
        var input = store.BuildSendInput(stored, RequestInfo.Empty, messageId: 42);
        input.Message.ShouldBe("top secret");
    }

    [RequiresMongoDbFact]
    public async Task Encryption_off_keeps_the_plaintext_as_is()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");
        var store = CreateStore(mongo.Database, new FakeEncryptionHelper());

        var item = ScheduledMessageStoreTests.Item() with { Message = "hello", ScheduleDate = 1_700_000_000 };
        await store.SaveAsync([new ScheduledQueueItem(item, null, null)], RequestInfo.Empty with { Layer = 222 });

        var stored = await collection.Find(FilterDefinition<ScheduledMessageDocument>.Empty).SingleAsync();
        stored.Item.Message.ShouldBe("hello");
        store.BuildSendInput(stored, RequestInfo.Empty, messageId: 1).Message.ShouldBe("hello");
    }

    [Fact]
    public async Task A_private_queue_notifies_only_the_sender()
    {
        var store = CreateStore(database: null!, new FakeEncryptionHelper());

        var audience = await store.GetQueueAudienceAsync(new Peer(PeerType.User, PeerUserId), UserId);

        audience.ShouldBe([UserId]);
    }

    [Fact]
    public async Task A_broadcast_channel_notifies_the_creator_and_every_admin_that_can_post()
    {
        const long creatorId = 900;
        const long postingAdmin = 901;
        const long otherAdmin = 902;

        var channelReadModel = new Mock<IChannelReadModel>();
        channelReadModel.SetupGet(p => p.Broadcast).Returns(true);
        channelReadModel.SetupGet(p => p.CreatorId).Returns(creatorId);
        channelReadModel.SetupGet(p => p.AdminList).Returns([
            Admin(postingAdmin, canPost: true),
            Admin(otherAdmin, canPost: false)
        ]);

        var channelAppService = new Mock<IChannelAppService>();
        channelAppService.Setup(p => p.GetAsync(ChannelId)).ReturnsAsync(channelReadModel.Object);

        var store = CreateStore(database: null!, new FakeEncryptionHelper(), channelAppService.Object);

        var audience = await store.GetQueueAudienceAsync(new Peer(PeerType.Channel, ChannelId), senderUserId: UserId);

        // Author + creator + the admin allowed to post; the admin without posting rights is excluded.
        audience.ShouldBe([UserId, creatorId, postingAdmin], ignoreOrder: true);
    }

    private static ChatAdmin Admin(long userId, bool canPost) =>
        new(promotedBy: 1, canEdit: true, userId: userId,
            adminRights: new ChatAdminRights { PostMessages = canPost }, rank: string.Empty);

    private static ScheduledMessageStore CreateStore(IMongoDatabase database, IMessageEncryptionHelper encryption,
        IChannelAppService? channelAppService = null)
    {
        return new ScheduledMessageStore(database,
            Mock.Of<IMessageConverterService>(),
            channelAppService ?? Mock.Of<IChannelAppService>(),
            Mock.Of<IChannelAdminRightsChecker>(),
            Mock.Of<IUserAppService>(),
            Mock.Of<IPeerHelper>(),
            Mock.Of<IPrivacyAppService>(),
            encryption);
    }

    /// <summary>
    /// Deterministic stand-in for the AES-GCM helper: it just wraps and unwraps the bytes so the tests
    /// can prove the plaintext round-trips without pulling in key configuration.
    /// </summary>
    private sealed class FakeEncryptionHelper : IMessageEncryptionHelper
    {
        private const string Prefix = "enc:";

        public bool IsEnabled => true;

        public byte[] Encrypt(long ownerPeerId, string message) =>
            System.Text.Encoding.UTF8.GetBytes($"{Prefix}{ownerPeerId}:{message}");

        public string Decrypt(long ownerPeerId, int messageId, ReadOnlyMemory<byte> encryptedData)
        {
            var text = System.Text.Encoding.UTF8.GetString(encryptedData.Span);
            return text[$"{Prefix}{ownerPeerId}:".Length..];
        }
    }
}
