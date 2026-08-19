using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.VideoProcessing;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.VideoProcessing;

/// <summary>
/// Feature: automatic video processing.
///
/// <para>
/// "Sending even non-scheduled videos to big channels will automatically trigger server-side
/// processing (i.e. to generate alternative qualities...)". The decision has to be narrow: only real
/// videos, only broadcast channels above the configured size, and only when there is a smaller quality
/// worth producing — everything else must keep going out immediately.
/// See https://corefork.telegram.org/api/scheduled-messages#automatic-video-processing
/// </para>
/// </summary>
public class VideoProcessingServiceTests
{
    private const long ChannelId = 1000001;

    [Fact]
    public async Task A_tall_video_posted_to_a_big_channel_is_processed()
    {
        var service = CreateService(participants: 100);

        (await service.ShouldProcessAsync(Video(height: 1080), Channel())).ShouldBeTrue();
    }

    [Fact]
    public async Task A_video_sent_to_a_user_is_never_processed()
    {
        var service = CreateService(participants: 100);

        (await service.ShouldProcessAsync(Video(height: 1080), new Peer(PeerType.User, 2010001))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_small_channel_keeps_its_videos_untouched()
    {
        var service = CreateService(participants: 5, minChannelParticipants: 50);

        (await service.ShouldProcessAsync(Video(height: 1080), Channel())).ShouldBeFalse();
    }

    [Fact]
    public async Task A_megagroup_keeps_its_videos_untouched()
    {
        var service = CreateService(participants: 100, broadcast: false);

        (await service.ShouldProcessAsync(Video(height: 1080), Channel())).ShouldBeFalse();
    }

    [Fact]
    public async Task A_video_that_is_already_smaller_than_every_rung_is_not_processed()
    {
        var service = CreateService(participants: 100);

        // The smallest configured rung is 360p, so a 360p upload has nothing to be converted into.
        (await service.ShouldProcessAsync(Video(height: 360), Channel())).ShouldBeFalse();
    }

    [Fact]
    public async Task Round_videos_animations_photos_and_plain_text_are_not_processed()
    {
        var service = CreateService(participants: 100);

        (await service.ShouldProcessAsync(Video(height: 1080, roundMessage: true), Channel())).ShouldBeFalse();
        (await service.ShouldProcessAsync(Video(height: 1080, animated: true), Channel())).ShouldBeFalse();
        (await service.ShouldProcessAsync(new TMessageMediaPhoto(), Channel())).ShouldBeFalse();
        (await service.ShouldProcessAsync(null, Channel())).ShouldBeFalse();
    }

    [Fact]
    public async Task Oversized_and_overlong_videos_are_delivered_as_they_are()
    {
        var service = CreateService(participants: 100);

        (await service.ShouldProcessAsync(Video(height: 1080, size: 1024L * 1024 * 1024), Channel())).ShouldBeFalse();
        (await service.ShouldProcessAsync(Video(height: 1080, duration: 4 * 3600), Channel())).ShouldBeFalse();
    }

    [Fact]
    public async Task Processing_can_be_switched_off()
    {
        var service = CreateService(participants: 100, enabled: false);

        (await service.ShouldProcessAsync(Video(height: 1080), Channel())).ShouldBeFalse();
    }

    [Fact]
    public void The_estimated_conversion_date_grows_with_the_video_and_never_undercuts_the_minimum()
    {
        var service = CreateService(participants: 100);

        // Three rungs at half a second of work per second of video.
        service.EstimateConversionSeconds(Video(height: 1080, duration: 600)).ShouldBe(900);
        service.EstimateConversionSeconds(Video(height: 1080, duration: 1)).ShouldBe(15);
        service.EstimateConversionSeconds(null).ShouldBe(15);
    }

    private static Peer Channel() => new(PeerType.Channel, ChannelId);

    private static IMessageMedia Video(int height, long size = 10 * 1024 * 1024, int duration = 30,
        bool roundMessage = false, bool animated = false)
    {
        var attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeVideo
        {
            W = height * 16 / 9,
            H = height,
            Duration = duration,
            RoundMessage = roundMessage
        });

        if (animated)
        {
            attributes.Add(new TDocumentAttributeAnimated());
        }

        return new TMessageMediaDocument
        {
            Document = new TDocument
            {
                Id = 777,
                AccessHash = 42,
                MimeType = "video/mp4",
                Size = size,
                DcId = 2,
                FileReference = new byte[] { 1, 2, 3 },
                Attributes = attributes
            }
        };
    }

    private static VideoProcessingService CreateService(int participants, bool broadcast = true, bool enabled = true,
        int minChannelParticipants = 1)
    {
        var options = new MyTelegramMessengerServerOptions();
        options.VideoProcessing.Enabled = enabled;
        options.VideoProcessing.MinChannelParticipants = minChannelParticipants;

        var channelReadModel = new Mock<IChannelReadModel>();
        channelReadModel.SetupGet(p => p.Broadcast).Returns(broadcast);
        channelReadModel.SetupGet(p => p.ParticipantsCount).Returns(participants);

        var channelAppService = new Mock<IChannelAppService>();
        channelAppService.Setup(p => p.GetAsync(ChannelId)).ReturnsAsync(channelReadModel.Object);

        return new VideoProcessingService(new StubOptionsMonitor(options), channelAppService.Object,
            Mock.Of<IStoredFileStorage>(), Mock.Of<IVideoTranscoder>(),
            NullLogger<VideoProcessingService>.Instance);
    }

    private sealed class StubOptionsMonitor(MyTelegramMessengerServerOptions value)
        : IOptionsMonitor<MyTelegramMessengerServerOptions>
    {
        public MyTelegramMessengerServerOptions CurrentValue { get; } = value;
        public MyTelegramMessengerServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MyTelegramMessengerServerOptions, string?> listener) => null;
    }
}
