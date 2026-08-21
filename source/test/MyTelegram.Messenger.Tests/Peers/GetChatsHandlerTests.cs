using System.Reflection;
using EventFlow.Queries;
using Moq;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: <c>messages.getChats</c>, the third bulk refresh method of the
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>.
///
/// <para>
/// It used to throw <c>NotImplementedException</c> for every call, so a client refreshing its cache
/// got a 500 instead of an answer. It takes bare ids with no access hash, because basic groups have
/// none — which means an id that resolves to a channel must not hand back that channel's details
/// unless the caller can actually read it.
/// </para>
/// </summary>
public class GetChatsHandlerTests
{
    private const long CallerUserId = 2_000_001;
    private const long ReadableChannelId = 800_000_000_001;
    private const long PrivateChannelId = 800_000_000_002;
    private const long BasicGroupId = 700_000_000_001;

    [Fact]
    public async Task A_readable_channel_comes_back()
    {
        var handler = CreateHandler();

        var chats = await InvokeAsync(handler, ReadableChannelId);

        chats.Chats.Count.ShouldBe(1);
        chats.Chats[0].ShouldBeOfType<TChannel>().Id.ShouldBe(ReadableChannelId);
    }

    [Fact]
    public async Task An_id_in_the_basic_group_range_answers_chatEmpty()
    {
        // Testgram stores every group as a channel, so nothing ever lives in the basic-group range.
        var handler = CreateHandler();

        var chats = await InvokeAsync(handler, BasicGroupId);

        chats.Chats.Count.ShouldBe(1);
        chats.Chats[0].ShouldBeOfType<TChatEmpty>().Id.ShouldBe(BasicGroupId);
    }

    [Fact]
    public async Task A_channel_the_caller_cannot_read_answers_chatEmpty_rather_than_leaking_it()
    {
        // The request carries no access hash, so an unreadable channel must not be distinguishable
        // from one that does not exist.
        var handler = CreateHandler();

        var chats = await InvokeAsync(handler, PrivateChannelId);

        chats.Chats.Count.ShouldBe(1);
        chats.Chats[0].ShouldBeOfType<TChatEmpty>().Id.ShouldBe(PrivateChannelId);
    }

    [Fact]
    public async Task The_reply_lines_up_position_by_position_with_the_request()
    {
        var handler = CreateHandler();

        var chats = await InvokeAsync(handler, BasicGroupId, ReadableChannelId, PrivateChannelId);

        chats.Chats.Count.ShouldBe(3);
        chats.Chats[0].ShouldBeOfType<TChatEmpty>().Id.ShouldBe(BasicGroupId);
        chats.Chats[1].ShouldBeOfType<TChannel>().Id.ShouldBe(ReadableChannelId);
        chats.Chats[2].ShouldBeOfType<TChatEmpty>().Id.ShouldBe(PrivateChannelId);
    }

    [Fact]
    public async Task A_user_id_is_CHAT_ID_INVALID()
    {
        var handler = CreateHandler();

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(handler, CallerUserId));

        exception.RpcError.Message.ShouldBe("CHAT_ID_INVALID");
    }

    [Fact]
    public async Task An_empty_request_is_PEER_ID_INVALID()
    {
        var handler = CreateHandler();

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(handler));

        exception.RpcError.Message.ShouldBe("PEER_ID_INVALID");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static object CreateHandler()
    {
        var readable = ChannelReadModel(ReadableChannelId);
        var priv = ChannelReadModel(PrivateChannelId);

        var channelAppService = new Mock<IChannelAppService>(MockBehavior.Loose);
        channelAppService.Setup(p => p.GetAsync(It.IsAny<long?>()))
            .ReturnsAsync((long? id) => id == ReadableChannelId ? readable : id == PrivateChannelId ? priv : null);
        channelAppService
            .Setup(p => p.HasReadAccessAsync(It.IsAny<long>(), It.IsAny<IChannelReadModel>()))
            .ReturnsAsync((long _, IChannelReadModel channel) => channel.ChannelId == ReadableChannelId);

        var chatConverterService = new Mock<IChatConverterService>(MockBehavior.Loose);
        chatConverterService
            .Setup(p => p.GetChannelListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                It.IsAny<IReadOnlyCollection<IChannelMemberReadModel>>(), It.IsAny<int>()))
            .ReturnsAsync((IRequestWithAccessHashKeyId _, List<long> ids,
                    IReadOnlyCollection<IChannelMemberReadModel>? _, int _) =>
                ids.Select(id => (IChat)new TChannel { Id = id, Title = "channel" }).ToList());

        var queryProcessor = new Mock<IQueryProcessor>(MockBehavior.Loose);
        queryProcessor
            .Setup(p => p.ProcessAsync(It.IsAny<GetChannelMemberListByChannelIdListQuery>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<IChannelMemberReadModel>)[]);

        var type = typeof(IChannelAppService).Assembly.GetType(
            "MyTelegram.Messenger.Handlers.LatestLayer.Messages.GetChatsHandler", throwOnError: true)!;

        return Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [new PeerHelper(), channelAppService.Object, chatConverterService.Object, queryProcessor.Object],
            culture: null)!;
    }

    private static IChannelReadModel ChannelReadModel(long channelId)
    {
        var channel = new Mock<IChannelReadModel>(MockBehavior.Loose);
        channel.SetupGet(p => p.ChannelId).Returns(channelId);
        channel.SetupGet(p => p.IsDeleted).Returns(false);

        return channel.Object;
    }

    private static async Task<MyTelegram.Schema.Messages.TChats> InvokeAsync(object handler, params long[] ids)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(CallerUserId);

        var request = new MyTelegram.Schema.Messages.RequestGetChats { Id = new TVector<long>(ids) };
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

        return (MyTelegram.Schema.Messages.TChats)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
