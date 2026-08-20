using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Domain.Extensions;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Scheduled;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Scheduled;

/// <summary>
/// Feature: scheduled messages — flushing the queue.
///
/// <para>
/// When a queued message is sent the client is told twice: the message itself arrives with the
/// <c>from_scheduled</c> flag, and <c>updateDeleteScheduledMessages</c> reports that the entry left the
/// queue, pairing the scheduled id with the real one at the same vector index. A repeating message goes
/// straight back into the queue. See https://corefork.telegram.org/api/scheduled-messages
/// </para>
/// </summary>
public class ScheduledMessageDispatcherTests
{
    private const long UserId = 2010001;
    private const long PeerUserId = 2010002;

    static ScheduledMessageDispatcherTests()
    {
        ScheduledTestSerializers.EnsureRegistered();
    }

    [RequiresMongoDbFact]
    public async Task Flushing_sends_the_messages_and_reports_the_real_ids()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var documents = new[]
        {
            ScheduledMessageStoreTests.Document(scheduledMessageId: 11, scheduleDate: 1_700_000_000),
            ScheduledMessageStoreTests.Document(scheduledMessageId: 12, scheduleDate: 1_700_000_000)
        };
        await collection.InsertManyAsync(documents);

        var sentInputs = new List<SendMessageInput>();
        var messageAppService = new Mock<IMessageAppService>();
        messageAppService
            .Setup(p => p.SendMessageAsync(It.IsAny<List<SendMessageInput>>()))
            .Callback<List<SendMessageInput>>(inputs => sentInputs.AddRange(inputs))
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(mongo.Database, messageAppService.Object, out _);

        var updates = (TUpdates)await dispatcher.FlushAsync(documents);

        // Every queued message is sent once, with a fresh message id and the from_scheduled marker.
        sentInputs.Count.ShouldBe(2);
        sentInputs.ShouldAllBe(p => p.FromScheduled);
        sentInputs.ShouldAllBe(p => p.ScheduleDate == null);
        sentInputs.Select(p => p.MessageId).ShouldBe([5001, 5002]);

        var update = updates.Updates.Single().ShouldBeOfType<TUpdateDeleteScheduledMessages>();
        update.Messages.ShouldBe([11, 12]);
        update.SentMessages!.ShouldBe([5001, 5002]);

        (await collection.CountDocumentsAsync(FilterDefinition<ScheduledMessageDocument>.Empty)).ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task A_repeating_message_goes_back_into_the_queue()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<ScheduledMessageDocument>("scheduled_messages");

        var document = ScheduledMessageStoreTests.Document(scheduledMessageId: 11, scheduleDate: 1_700_000_000,
            repeatPeriod: 86400);
        await collection.InsertOneAsync(document);

        var messageAppService = new Mock<IMessageAppService>();
        messageAppService.Setup(p => p.SendMessageAsync(It.IsAny<List<SendMessageInput>>()))
            .Returns(Task.CompletedTask);

        var dispatcher = CreateDispatcher(mongo.Database, messageAppService.Object, out _);

        await dispatcher.FlushAsync([document]);

        var queued = await collection.Find(FilterDefinition<ScheduledMessageDocument>.Empty).SingleAsync();
        queued.ScheduledMessageId.ShouldNotBe(11);
        queued.RepeatPeriod.ShouldBe(86400);
        queued.ScheduleDate.ShouldBeGreaterThan(DateTime.UtcNow.ToTimestamp() + 86400 - 60);
        // A resent message needs its own random id, the old one was already used.
        queued.RandomId.ShouldNotBe(document.RandomId);
    }

    private static ScheduledMessageDispatcher CreateDispatcher(IMongoDatabase database,
        IMessageAppService messageAppService, out Mock<IObjectMessageSender> objectMessageSender)
    {
        var peerHelper = new Mock<IPeerHelper>();
        peerHelper.Setup(p => p.ToPeer(It.IsAny<Peer>()))
            .Returns<Peer>(peer => new TPeerUser { UserId = peer.PeerId });

        var messageConverterService = new Mock<IMessageConverterService>();
        messageConverterService.Setup(p => p.ToMessage(It.IsAny<long>(), It.IsAny<MessageItem>(),
                It.IsAny<List<long>?>(), It.IsAny<bool>(), It.IsAny<int>()))
            .Returns<long, MessageItem, List<long>?, bool, int>((_, item, _, _, _) =>
                new TMessage { Id = item.MessageId, Message = item.Message, PeerId = new TPeerUser { UserId = item.ToPeer.PeerId } });

        var store = new ScheduledMessageStore(database, messageConverterService.Object, null!, null!, null!,
            peerHelper.Object, null!, null!);

        var nextId = 5000;
        var idGenerator = new Mock<IIdGenerator>();
        idGenerator.Setup(p => p.NextIdAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++nextId);

        objectMessageSender = new Mock<IObjectMessageSender>();
        objectMessageSender.Setup(p => p.PushMessageToPeerAsync(It.IsAny<Peer>(), It.IsAny<TUpdates>(),
                It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(),
                It.IsAny<int?>(), It.IsAny<long>(), It.IsAny<PushData?>(), It.IsAny<List<long>?>()))
            .Returns(Task.CompletedTask);

        return new ScheduledMessageDispatcher(store, messageAppService, idGenerator.Object,
            objectMessageSender.Object, NullLogger<ScheduledMessageDispatcher>.Instance);
    }
}
