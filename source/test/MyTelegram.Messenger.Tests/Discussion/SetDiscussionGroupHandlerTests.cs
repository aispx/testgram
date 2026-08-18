using System.Reflection;
using EventFlow;
using EventFlow.Queries;
using MongoDB.Driver;
using MyTelegram.Domain.Aggregates.Temp;
using Moq;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Channels;

namespace MyTelegram.Messenger.Tests.Discussion;

/// <summary>
/// Feature: discussion groups — linking and unlinking a group to a channel.
///
/// <para>
/// <c>channels.setDiscussionGroup</c> is the only way to create or remove a comment section, and it is
/// reached with an <c>inputChannelEmpty</c> group whenever the owner unlinks — a shape that used to fall
/// through the admin-rights checker into an unhandled <c>ArgumentOutOfRangeException</c> instead of doing
/// the work. These tests pin the accepted shapes and the documented rejections.
/// See https://corefork.telegram.org/method/channels.setDiscussionGroup
/// </para>
/// </summary>
public class SetDiscussionGroupHandlerTests
{
    private const long OwnerUserId = 42;
    private const long BroadcastId = 800_000_000_001;
    private const long GroupId = 800_000_000_002;

    [RequiresMongoDbFact]
    public async Task Linking_a_group_publishes_the_command()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, commandBus,
            Broadcast(linkedChatId: null),
            MegaGroup(hiddenPreHistory: false));

        var result = await InvokeAsync(handler, Request(new TInputChannel { ChannelId = GroupId }));

        result.ShouldBeNull();
        commandBus.Verify(p => p.PublishAsync(
            It.Is<StartSetChannelDiscussionGroupCommand>(c =>
                c.BroadcastChannelId == BroadcastId && c.DiscussionGroupChannelId == GroupId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task Unlinking_with_inputChannelEmpty_publishes_the_command_with_no_group()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var commandBus = new Mock<ICommandBus>(MockBehavior.Loose);
        var handler = CreateHandler(mongo.Database, commandBus,
            Broadcast(linkedChatId: GroupId),
            MegaGroup(hiddenPreHistory: false));

        var result = await InvokeAsync(handler, Request(new TInputChannelEmpty()));

        result.ShouldBeNull();
        commandBus.Verify(p => p.PublishAsync(
            It.Is<StartSetChannelDiscussionGroupCommand>(c =>
                c.BroadcastChannelId == BroadcastId && c.DiscussionGroupChannelId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [RequiresMongoDbFact]
    public async Task Unlinking_a_channel_that_has_no_discussion_group_is_LINK_NOT_MODIFIED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var handler = CreateHandler(mongo.Database, new Mock<ICommandBus>(MockBehavior.Loose),
            Broadcast(linkedChatId: null),
            MegaGroup(hiddenPreHistory: false));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, Request(new TInputChannelEmpty())));

        exception.RpcError.Message.ShouldBe("LINK_NOT_MODIFIED");
    }

    [RequiresMongoDbFact]
    public async Task Linking_the_group_that_is_already_linked_is_LINK_NOT_MODIFIED()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var handler = CreateHandler(mongo.Database, new Mock<ICommandBus>(MockBehavior.Loose),
            Broadcast(linkedChatId: GroupId),
            MegaGroup(hiddenPreHistory: false));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, Request(new TInputChannel { ChannelId = GroupId })));

        exception.RpcError.Message.ShouldBe("LINK_NOT_MODIFIED");
    }

    [RequiresMongoDbFact]
    public async Task A_group_with_hidden_prehistory_is_rejected()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var handler = CreateHandler(mongo.Database, new Mock<ICommandBus>(MockBehavior.Loose),
            Broadcast(linkedChatId: null),
            MegaGroup(hiddenPreHistory: true));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, Request(new TInputChannel { ChannelId = GroupId })));

        exception.RpcError.Message.ShouldBe("MEGAGROUP_PREHISTORY_HIDDEN");
    }

    [RequiresMongoDbFact]
    public async Task A_broadcast_channel_cannot_be_used_as_the_discussion_group()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var handler = CreateHandler(mongo.Database, new Mock<ICommandBus>(MockBehavior.Loose),
            Broadcast(linkedChatId: null),
            // The "group" resolves to another broadcast channel, not a supergroup.
            Broadcast(linkedChatId: null));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, Request(new TInputChannel { ChannelId = GroupId })));

        exception.RpcError.Message.ShouldBe("MEGAGROUP_ID_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_supergroup_cannot_be_used_as_the_broadcast_channel()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var handler = CreateHandler(mongo.Database, new Mock<ICommandBus>(MockBehavior.Loose),
            MegaGroup(hiddenPreHistory: false),
            MegaGroup(hiddenPreHistory: false));

        var exception = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(handler, Request(new TInputChannel { ChannelId = GroupId })));

        exception.RpcError.Message.ShouldBe("BROADCAST_ID_INVALID");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static RequestSetDiscussionGroup Request(IInputChannel group) => new()
    {
        Broadcast = new TInputChannel { ChannelId = BroadcastId },
        Group = group
    };

    private static IChannelReadModel Broadcast(long? linkedChatId)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.Broadcast).Returns(true);
        channel.SetupGet(p => p.MegaGroup).Returns(false);
        channel.SetupGet(p => p.CreatorId).Returns(OwnerUserId);
        channel.SetupGet(p => p.LinkedChatId).Returns(linkedChatId);

        return channel.Object;
    }

    private static IChannelReadModel MegaGroup(bool hiddenPreHistory)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.Broadcast).Returns(false);
        channel.SetupGet(p => p.MegaGroup).Returns(true);
        channel.SetupGet(p => p.CreatorId).Returns(OwnerUserId);
        channel.SetupGet(p => p.HiddenPreHistory).Returns(hiddenPreHistory);

        return channel.Object;
    }

    private static object CreateHandler(IMongoDatabase database,
        Mock<ICommandBus> commandBus,
        IChannelReadModel broadcast,
        IChannelReadModel group)
    {
        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>()))
            .ReturnsAsync((long? id) => id == BroadcastId ? broadcast : id == GroupId ? group : null);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long>()))
            .ReturnsAsync((long id) => id == BroadcastId ? broadcast : group);

        var checker = new ChannelAdminRightsChecker(new Mock<IQueryProcessor>(MockBehavior.Loose).Object,
            channelAppService.Object);

        var handlerType = typeof(ChannelAdminRightsChecker).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Channels.SetDiscussionGroupHandler",
            throwOnError: true)!;

        return Activator.CreateInstance(handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [commandBus.Object, checker, channelAppService.Object, database],
            culture: null)!;
    }

    private static async Task<object?> InvokeAsync(object handler, RequestSetDiscussionGroup request)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(OwnerUserId);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        return task.GetType().GetProperty("Result")!.GetValue(task);
    }
}
