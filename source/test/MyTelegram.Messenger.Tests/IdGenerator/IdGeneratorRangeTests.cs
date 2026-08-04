using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Queries;
using MyTelegram.Services.Services.IdGenerator;

namespace MyTelegram.Messenger.Tests.IdGenerator;

/// <summary>
/// Regression tests for <see cref="global::MyTelegram.Messenger.Services.Impl.IdGenerator"/>.
///
/// <para>When the generator started seeding its HiLo state from the largest id already stored in the
/// read models, it seeded that state with an id that <em>already</em> included the per-type offset from
/// <c>GetInitId</c>, and then added the offset a second time. For <see cref="IdType.UserId"/> the stored
/// maximum was also taken across the whole user collection — bots included — so a single registered bot
/// at 600000000029 pushed the next regular user to 600000000029 + 2010000 = 600002010029. That lands
/// inside the bot range, so <c>IPeerHelper.IsBotUser</c> reported every fresh user as a bot and
/// <c>messages.sendMessage</c> failed with <c>USER_IS_BOT</c> — even for Saved Messages.
/// </para>
/// </summary>
public class IdGeneratorRangeTests
{
    private const int BlockSize = 1000;

    private static global::MyTelegram.Messenger.Services.Impl.IdGenerator CreateGenerator(
        long maxUserId,
        long maxChannelId,
        out Mock<IHiLoHighValueGenerator> highValueGenerator)
    {
        var queryProcessor = new Mock<IQueryProcessor>();
        queryProcessor
            .Setup(x => x.ProcessAsync(It.IsAny<GetMaxUserIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(maxUserId);
        queryProcessor
            .Setup(x => x.ProcessAsync(It.IsAny<GetMaxChannelIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(maxChannelId);

        var blockSizeHelper = new Mock<IHiLoStateBlockSizeHelper>();
        blockSizeHelper.Setup(x => x.GetBlockSize(It.IsAny<IdType>())).Returns(BlockSize);

        highValueGenerator = new Mock<IHiLoHighValueGenerator>();
        var high = 0L;
        highValueGenerator
            .Setup(x => x.GetNewHighValueAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref high));

        var cache = new HiLoValueGeneratorCache(blockSizeHelper.Object);
        var factory = new HiLoValueGeneratorFactory(
            NullLogger<DefaultHiLoValueGenerator>.Instance,
            highValueGenerator.Object);

        return new global::MyTelegram.Messenger.Services.Impl.IdGenerator(
            cache,
            factory,
            queryProcessor.Object,
            blockSizeHelper.Object,
            NullLogger<global::MyTelegram.Messenger.Services.Impl.IdGenerator>.Instance);
    }

    [Fact]
    public async Task NextLongIdAsync_UserId_StaysBelowTheBotRange_WhenABotHoldsTheLargestStoredUserId()
    {
        // A bot occupies the largest UserId in the collection — exactly the production situation.
        var sut = CreateGenerator(maxUserId: MyTelegramConsts.BotUserInitId + 29, maxChannelId: 0, out _);

        var userId = await sut.NextLongIdAsync(IdType.UserId);

        userId.ShouldBeLessThan(MyTelegramConsts.BotUserInitId);
        userId.ShouldBeGreaterThanOrEqualTo(MyTelegramConsts.UserIdInitId);
    }

    [Fact]
    public async Task NextLongIdAsync_UserId_ContinuesAfterTheLargestRegularUser()
    {
        const long storedMax = 2094006;
        var sut = CreateGenerator(maxUserId: storedMax, maxChannelId: 0, out _);

        var userId = await sut.NextLongIdAsync(IdType.UserId);

        userId.ShouldBeGreaterThan(storedMax);
        userId.ShouldBeLessThan(MyTelegramConsts.BotUserInitId);
    }

    [Fact]
    public async Task NextLongIdAsync_UserId_IsSequentialAcrossCalls()
    {
        var sut = CreateGenerator(maxUserId: 2094006, maxChannelId: 0, out _);

        var first = await sut.NextLongIdAsync(IdType.UserId);
        var second = await sut.NextLongIdAsync(IdType.UserId);

        second.ShouldBe(first + 1);
    }

    [Fact]
    public async Task NextLongIdAsync_UserId_StaysInRangeOnAFreshDatabase()
    {
        var sut = CreateGenerator(maxUserId: 0, maxChannelId: 0, out _);

        var userId = await sut.NextLongIdAsync(IdType.UserId);

        userId.ShouldBeGreaterThanOrEqualTo(MyTelegramConsts.UserIdInitId);
        userId.ShouldBeLessThan(MyTelegramConsts.BotUserInitId);
    }

    [Fact]
    public async Task NextLongIdAsync_ChannelId_StaysInTheChannelRange()
    {
        const long storedMax = 800000090002;
        var sut = CreateGenerator(maxUserId: 0, maxChannelId: storedMax, out _);

        var channelId = await sut.NextLongIdAsync(IdType.ChannelId);

        channelId.ShouldBeGreaterThan(storedMax);
        channelId.ShouldBeGreaterThanOrEqualTo(MyTelegramConsts.ChannelInitId);
    }

    [Fact]
    public async Task NextLongIdAsync_BotUserId_StaysInTheBotRange()
    {
        var sut = CreateGenerator(maxUserId: MyTelegramConsts.BotUserInitId + 29, maxChannelId: 0, out _);

        var botUserId = await sut.NextLongIdAsync(IdType.BotUserId);

        botUserId.ShouldBeGreaterThanOrEqualTo(MyTelegramConsts.BotUserInitId);
        botUserId.ShouldBeLessThan(MyTelegramConsts.ChatIdInitId);
    }

    [Fact]
    public async Task NextLongIdAsync_UserId_ReadsTheStoredMaximumOnlyOncePerProcess()
    {
        var queryProcessor = new Mock<IQueryProcessor>();
        queryProcessor
            .Setup(x => x.ProcessAsync(It.IsAny<GetMaxUserIdQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2094006L);

        var blockSizeHelper = new Mock<IHiLoStateBlockSizeHelper>();
        blockSizeHelper.Setup(x => x.GetBlockSize(It.IsAny<IdType>())).Returns(BlockSize);

        var highValueGenerator = new Mock<IHiLoHighValueGenerator>();
        var high = 0L;
        highValueGenerator
            .Setup(x => x.GetNewHighValueAsync(It.IsAny<IdType>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref high));

        var cache = new HiLoValueGeneratorCache(blockSizeHelper.Object);
        var factory = new HiLoValueGeneratorFactory(
            NullLogger<DefaultHiLoValueGenerator>.Instance,
            highValueGenerator.Object);
        var sut = new global::MyTelegram.Messenger.Services.Impl.IdGenerator(
            cache,
            factory,
            queryProcessor.Object,
            blockSizeHelper.Object,
            NullLogger<global::MyTelegram.Messenger.Services.Impl.IdGenerator>.Instance);

        await sut.NextLongIdAsync(IdType.UserId);
        await sut.NextLongIdAsync(IdType.UserId);
        await sut.NextLongIdAsync(IdType.UserId);

        queryProcessor.Verify(
            x => x.ProcessAsync(It.IsAny<GetMaxUserIdQuery>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
