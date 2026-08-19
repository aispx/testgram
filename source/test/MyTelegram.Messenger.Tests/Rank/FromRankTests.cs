using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Converters.Responses;
using MyTelegram.Converters.TLObjects.Interfaces;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MyTelegram.Services.TLObjectConverters;

namespace MyTelegram.Messenger.Tests.Rank;

/// <summary>
/// Feature: message.from_rank — the tag of the sender attached to every supergroup message.
///
/// <para>
/// The tag has to be the <i>current</i> one, so it is resolved from the channel member read model when
/// the message is read rather than frozen into the message when it was sent. Channel posts carry no
/// sender and therefore no tag. See https://corefork.telegram.org/api/rank
/// </para>
/// </summary>
public class FromRankTests
{
    private const long ChannelId = 1_500_001;
    private const long SenderUserId = 2_010_001;
    private const long SelfUserId = 2_010_002;

    [RequiresMongoDbFact]
    public void The_current_tag_of_the_sender_is_attached_to_a_supergroup_message()
    {
        using var mongo = EmbeddedMongoServer.Start();
        InsertMember(mongo.Database, ChannelId, SenderUserId, "designer");

        var message = Convert(mongo.Database, ChannelReadModel(post: false));

        message.ShouldBeOfType<TMessage>().FromRank.ShouldBe("designer");
    }

    [RequiresMongoDbFact]
    public void A_member_without_a_tag_leaves_from_rank_unset()
    {
        using var mongo = EmbeddedMongoServer.Start();
        InsertMember(mongo.Database, ChannelId, SenderUserId, string.Empty);

        var message = Convert(mongo.Database, ChannelReadModel(post: false));

        message.ShouldBeOfType<TMessage>().FromRank.ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public void A_channel_post_carries_no_tag()
    {
        using var mongo = EmbeddedMongoServer.Start();
        InsertMember(mongo.Database, ChannelId, SenderUserId, "designer");

        var message = Convert(mongo.Database, ChannelReadModel(post: true));

        message.ShouldBeOfType<TMessage>().FromRank.ShouldBeNull();
    }

    [RequiresMongoDbFact]
    public void The_tag_of_a_member_of_another_group_is_not_used()
    {
        using var mongo = EmbeddedMongoServer.Start();
        InsertMember(mongo.Database, ChannelId + 1, SenderUserId, "designer");

        var message = Convert(mongo.Database, ChannelReadModel(post: false));

        message.ShouldBeOfType<TMessage>().FromRank.ShouldBeNull();
    }

    private static void InsertMember(IMongoDatabase database, long channelId, long userId, string rank)
    {
        database.GetCollection<BsonDocument>("eventflow-channelmemberreadmodel").InsertOne(new BsonDocument
        {
            { "_id", ChannelMemberId.Create(channelId, userId).Value },
            { "ChannelId", channelId },
            { "UserId", userId },
            { "Rank", rank }
        });
    }

    private static IMessage Convert(IMongoDatabase database, IMessageReadModel readModel)
    {
        var messageConverter = new Mock<IMessageConverter>();
        messageConverter
            .Setup(p => p.ToMessage(It.IsAny<IMessageReadModel>()))
            .Returns(() => new TMessage { Id = readModel.MessageId, Message = string.Empty });

        var messageLayeredService = new Mock<ILayeredService<IMessageConverter>>();
        messageLayeredService.Setup(p => p.GetConverter(It.IsAny<int>())).Returns(messageConverter.Object);

        var mediaResponseService = new Mock<IMessageMediaResponseService>();
        mediaResponseService
            .Setup(p => p.ToLayeredData(It.IsAny<IMessageMedia?>(), It.IsAny<int>()))
            .Returns((IMessageMedia? media, int _) => media);

        var options = new Mock<IOptionsMonitor<MyTelegramMessengerServerOptions>>();
        options.Setup(p => p.CurrentValue).Returns(new MyTelegramMessengerServerOptions());

        var service = new MessageConverterService(
            mediaResponseService.Object,
            messageLayeredService.Object,
            Mock.Of<ILayeredService<IMessageServiceConverter>>(),
            Mock.Of<ILayeredService<IMessageFwdHeaderConverter>>(),
            Mock.Of<ILayeredService<IPollConverter>>(),
            Mock.Of<IDataEncryptionHelper>(),
            options.Object,
            database,
            NullLogger<MessageConverterService>.Instance);

        return service.ToMessage(SelfUserId, readModel);
    }

    private static IMessageReadModel ChannelReadModel(bool post)
    {
        var readModel = new Mock<IMessageReadModel>();
        readModel.SetupGet(p => p.SendMessageType).Returns(SendMessageType.Text);
        readModel.SetupGet(p => p.SenderPeerId).Returns(SenderUserId);
        readModel.SetupGet(p => p.OwnerPeerId).Returns(SelfUserId);
        readModel.SetupGet(p => p.MessageId).Returns(42);
        readModel.SetupGet(p => p.ToPeerType).Returns(PeerType.Channel);
        readModel.SetupGet(p => p.ToPeerId).Returns(ChannelId);
        readModel.SetupGet(p => p.Post).Returns(post);
        readModel.SetupGet(p => p.Media2).Returns(new TMessageMediaEmpty());

        return readModel.Object;
    }
}
