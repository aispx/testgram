using MongoDB.Bson;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Stories;

/// <summary>
/// Feature: stories — converting a stored story to its TL form.
///
/// <para>
/// Two things made the profile's story section come back empty. First, expiry alone collapsed a
/// story to <c>storyItemDeleted</c>; per the
/// <a href="https://corefork.telegram.org/api/stories">API</a> expiry only moves a story to the
/// archive, and pinning puts it back on the profile — so
/// <c>stories.getPinnedStories</c> and <c>stories.getStoriesArchive</c>, whose entire purpose is
/// serving expired stories, returned nothing but tombstones. Second, photo sizes were invented from
/// the story document's <c>MediaSize</c>, which is 0 for every photo story here, so the client was
/// told to fetch a 100 KB image that does not exist at that length and the picture never rendered.
/// </para>
/// </summary>
public class StoryItemConversionTests
{
    private const long OwnerId = 2010001;
    private const long PhotoId = 5328746406449161431;

    private static int Now => (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static StoryDocument ExpiredPhotoStory(int storyId = 11001, bool pinned = true) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OwnerPeerId = OwnerId,
        OwnerPeerType = StoryHelper.PeerTypeUser,
        StoryId = storyId,
        Date = Now - 200_000,
        ExpireDate = Now - 100_000,
        Pinned = pinned,
        Deleted = false,
        Archived = true,
        MediaType = StoryHelper.MediaTypePhoto,
        MediaFileId = PhotoId,
        MediaAccessHash = 6380161177511521543,
        MediaDcId = 2,
        // Every photo story on this server has MediaSize = 0 — the case the guess had to cover.
        MediaSize = 0
    };

    /// <summary>The real read model for <see cref="PhotoId"/>, as stored in photoreadmodel.</summary>
    private sealed class FakePhotoReadModel : IPhotoReadModel
    {
        public string Id => $"photo-{PhotoId}";
        public long AccessHash => 6380161177511521543;
        public int Date => 1774791566;
        public int DcId => 2;
        public byte[] FileReference => [1, 2, 3, 4];
        public bool HasStickers => false;
        public bool HasVideo => false;
        public long PhotoId => StoryItemConversionTests.PhotoId;
        public long Size => 433033;
        public long UserId => OwnerId;
        public bool IsProfilePhoto => false;
        public List<VideoSize>? VideoSizes => null;
        public List<IPhotoSize>? Sizes2 => null;
        public List<IVideoSize>? VideoSizes2 => null;

        public List<PhotoSize>? Sizes =>
        [
            new(180, 320, 10719, "m"),
            new(450, 800, 43283, "x"),
            new(720, 1280, 89123, "y"),
            new(1440, 2560, 292980, "w")
        ];
    }

    [Fact]
    public void An_expired_pinned_story_is_not_reported_as_deleted()
    {
        // The defect that emptied the profile: pinning exists precisely so an expired story stays
        // visible, so expiry must not turn it into a tombstone.
        var item = StoryHelper.ConvertToStoryItem(ExpiredPhotoStory(), OwnerId);

        item.ShouldBeOfType<TStoryItem>().Id.ShouldBe(11001);
    }

    [Fact]
    public void An_expired_story_still_reports_its_expiry_date()
    {
        var doc = ExpiredPhotoStory();

        var item = StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();

        // The client is told the truth about expiry; it just is not told the story is gone.
        item.ExpireDate.ShouldBe((int)doc.ExpireDate);
        item.ExpireDate.ShouldBeLessThan(Now);
    }

    [Fact]
    public void A_genuinely_deleted_story_is_still_reported_as_deleted()
    {
        var doc = ExpiredPhotoStory();
        doc.Deleted = true;

        var item = StoryHelper.ConvertToStoryItem(doc, OwnerId);

        item.ShouldBeOfType<TStoryItemDeleted>().Id.ShouldBe(11001);
    }

    [Fact]
    public void An_unexpired_story_is_unaffected()
    {
        var doc = ExpiredPhotoStory();
        doc.ExpireDate = Now + 100_000;

        StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();
    }

    [Fact]
    public void Real_photo_sizes_are_used_when_the_read_model_is_supplied()
    {
        var item = StoryHelper
            .ConvertToStoryItem(ExpiredPhotoStory(), OwnerId, photo: new FakePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        var photo = item.Media.ShouldBeOfType<TMessageMediaPhoto>()
            .Photo.ShouldBeOfType<TPhoto>();

        photo.Sizes.Select(s => ((TPhotoSize)s).Type).ShouldBe(["m", "x", "y", "w"]);
        photo.Sizes.Select(s => ((TPhotoSize)s).Size).ShouldBe([10719, 43283, 89123, 292980]);
    }

    [Fact]
    public void Real_photo_sizes_replace_the_invented_ones()
    {
        var doc = ExpiredPhotoStory();

        var guessed = StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();
        var real = StoryHelper.ConvertToStoryItem(doc, OwnerId, photo: new FakePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        // Without a photo row there is nothing trustworthy to offer beyond the base object.
        var guessedSizes = ((TPhoto)((TMessageMediaPhoto)guessed.Media).Photo).Sizes;
        guessedSizes.Count.ShouldBe(1);

        // With one, the client gets the real per-size breakdown it can actually download.
        var realSizes = ((TPhoto)((TMessageMediaPhoto)real.Media).Photo).Sizes;
        realSizes.Select(s => ((TPhotoSize)s).Size).ShouldBe([10719, 43283, 89123, 292980]);
    }

    [Fact]
    public void Photo_identity_still_comes_from_the_story_document()
    {
        // Access hash and file reference are stored on the story and already agree with the photo
        // read model; only the size breakdown was missing.
        var item = StoryHelper
            .ConvertToStoryItem(ExpiredPhotoStory(), OwnerId, photo: new FakePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        var photo = ((TMessageMediaPhoto)item.Media).Photo.ShouldBeOfType<TPhoto>();
        photo.Id.ShouldBe(PhotoId);
        photo.AccessHash.ShouldBe(6380161177511521543);
        photo.DcId.ShouldBe(2);
    }

    [Fact]
    public void A_story_with_no_media_is_still_reported_as_deleted()
    {
        var doc = ExpiredPhotoStory();
        doc.MediaFileId = 0;

        StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItemDeleted>();
    }

    [Fact]
    public void Falling_back_to_a_guess_still_works_without_a_read_model()
    {
        // Not every caller loads photos; those must keep producing a usable, if approximate, answer.
        var doc = ExpiredPhotoStory();
        doc.MediaSize = 400_000;

        var item = StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes.Select(s => ((TPhotoSize)s).Size).ShouldContain(400_000);
    }

    [Fact]
    public void With_no_photo_row_and_no_stored_size_no_length_is_invented()
    {
        // Advertising a made-up length is worse than advertising none: the client fetches it, the
        // file-server 404s because no such object exists, and the client retries the same missing
        // size. This was 132 of 178 observed file-server errors, and it is what made stories crawl.
        var doc = ExpiredPhotoStory();
        doc.MediaSize = 0;

        var item = StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes.Count.ShouldBe(1);
        sizes.Select(s => ((TPhotoSize)s).Size).ShouldNotContain(100000);
    }

    [Fact]
    public void Only_sizes_present_in_the_read_model_are_advertised()
    {
        // The read model is the contract with storage: if it lists m and x, the response must not
        // also offer y or w. 154 of 326 live photos declared sizes with no object behind them.
        var item = StoryHelper
            .ConvertToStoryItem(ExpiredPhotoStory(), OwnerId, photo: new TwoSizePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes.Select(s => ((TPhotoSize)s).Type).ShouldBe(["m", "x"]);
    }

    /// <summary>A photo whose storage really only holds the "m" and "x" objects.</summary>
    private sealed class TwoSizePhotoReadModel : IPhotoReadModel
    {
        public string Id => $"photo-{PhotoId}";
        public long AccessHash => 6380161177511521543;
        public int Date => 1774791566;
        public int DcId => 2;
        public byte[] FileReference => [1, 2, 3, 4];
        public bool HasStickers => false;
        public bool HasVideo => false;
        public long PhotoId => StoryItemConversionTests.PhotoId;
        public long Size => 433033;
        public long UserId => OwnerId;
        public bool IsProfilePhoto => false;
        public List<VideoSize>? VideoSizes => null;
        public List<IPhotoSize>? Sizes2 => null;
        public List<IVideoSize>? VideoSizes2 => null;

        public List<PhotoSize>? Sizes => [new(180, 320, 10719, "m"), new(450, 800, 43283, "x")];
    }

    private const long VideoDocumentId = 4649901531927009243;

    private static StoryDocument VideoStory() => new()
    {
        Id = ObjectId.GenerateNewId(),
        OwnerPeerId = OwnerId,
        OwnerPeerType = StoryHelper.PeerTypeUser,
        StoryId = 19002,
        Date = Now - 200_000,
        ExpireDate = Now - 100_000,
        Pinned = true,
        Deleted = false,
        MediaType = StoryHelper.MediaTypeVideo,
        MediaFileId = VideoDocumentId,
        MediaAccessHash = 12345,
        MediaDcId = 2,
        MediaSize = 3107812,
        MediaMimeType = "video/mp4",
        VideoWidth = 720,
        VideoHeight = 1280,
        VideoDuration = 12
    };

    /// <summary>A video document whose thumbnail lives in storage, as the live rows do.</summary>
    private sealed class VideoDocumentReadModel : IDocumentReadModel
    {
        public string Id => $"documentreadmodel-{VideoDocumentId}";
        public long AccessHash => 12345;
        public byte[]? Attributes => null;
        public long? CreatorId => null;
        public int Date => 1775290238;
        public int DcId => 2;
        public long DocumentId => VideoDocumentId;
        public ReadOnlyMemory<byte> FileReference => new byte[] { 1, 2, 3, 4 };
        public int? Fingerprint => null;
        public string? Md5CheckSum => null;
        public string? Name => "video.mp4";
        public string MimeType => "video/mp4";
        public long Size => 3107812;
        public long? ThumbId => null;
        public long? VideoThumbId => null;
        public List<VideoSize>? VideoThumbs => null;
        public List<IDocumentAttribute>? Attributes2 => null;
        public List<PhotoSize>? Thumbs => [new(720, 1280, 44141, "y")];
    }

    [Fact]
    public void A_video_story_without_a_document_row_has_no_preview_thumbnail()
    {
        // The state that showed no preview: the story itself has no inline thumbnail, so unless the
        // document read model is supplied there is nothing for the client to draw.
        var item = StoryHelper.ConvertToStoryItem(VideoStory(), OwnerId).ShouldBeOfType<TStoryItem>();

        var doc = item.Media.ShouldBeOfType<TMessageMediaDocument>().Document.ShouldBeOfType<TDocument>();
        doc.Thumbs.ShouldBeEmpty();
    }

    [Fact]
    public void A_video_story_gets_its_thumbnail_from_the_document_row()
    {
        var item = StoryHelper
            .ConvertToStoryItem(VideoStory(), OwnerId, document: new VideoDocumentReadModel())
            .ShouldBeOfType<TStoryItem>();

        var doc = item.Media.ShouldBeOfType<TMessageMediaDocument>().Document.ShouldBeOfType<TDocument>();
        var thumb = doc.Thumbs.ShouldHaveSingleItem().ShouldBeOfType<TPhotoSize>();
        thumb.Type.ShouldBe("y");
        thumb.Size.ShouldBe(44141);
        thumb.W.ShouldBe(720);
        thumb.H.ShouldBe(1280);
    }

    [Fact]
    public void An_inline_stripped_thumbnail_comes_first_when_the_upload_captured_one()
    {
        // A stripped thumb renders instantly, so it should precede the downloadable size.
        var story = VideoStory();
        story.StrippedThumbBytes = [1, 2, 3];

        var item = StoryHelper
            .ConvertToStoryItem(story, OwnerId, document: new VideoDocumentReadModel())
            .ShouldBeOfType<TStoryItem>();

        var doc = (TDocument)((TMessageMediaDocument)item.Media).Document;
        doc.Thumbs!.Count.ShouldBe(2);
        doc.Thumbs[0].ShouldBeOfType<TPhotoStrippedSize>().Type.ShouldBe("i");
        doc.Thumbs[1].ShouldBeOfType<TPhotoSize>().Type.ShouldBe("y");
    }

    [Fact]
    public void The_video_document_keeps_its_identity_and_attributes()
    {
        var item = StoryHelper
            .ConvertToStoryItem(VideoStory(), OwnerId, document: new VideoDocumentReadModel())
            .ShouldBeOfType<TStoryItem>();

        var doc = (TDocument)((TMessageMediaDocument)item.Media).Document;
        doc.Id.ShouldBe(VideoDocumentId);
        doc.MimeType.ShouldBe("video/mp4");
        doc.Size.ShouldBe(3107812);
        var video = doc.Attributes.ShouldHaveSingleItem().ShouldBeOfType<TDocumentAttributeVideo>();
        video.W.ShouldBe(720);
        video.Duration.ShouldBe(12);
    }

    [Fact]
    public void A_photo_story_offers_its_inline_preview_first()
    {
        // The client builds the profile tile from the stripped size and ignores a list without one,
        // so it has to be present and ahead of the downloadable sizes.
        var doc = ExpiredPhotoStory();
        doc.StrippedThumbBytes = [1, 2, 3];

        var item = StoryHelper
            .ConvertToStoryItem(doc, OwnerId, photo: new FakePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes[0].ShouldBeOfType<TPhotoStrippedSize>().Type.ShouldBe("i");
        sizes.Skip(1).Select(x => ((TPhotoSize)x).Type).ShouldBe(["m", "x", "y", "w"]);
    }

    [Fact]
    public void A_photo_story_without_a_read_model_still_offers_the_inline_preview()
    {
        var doc = ExpiredPhotoStory();
        doc.StrippedThumbBytes = [1, 2, 3];

        var item = StoryHelper.ConvertToStoryItem(doc, OwnerId).ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes[0].ShouldBeOfType<TPhotoStrippedSize>().Type.ShouldBe("i");
    }

    [Fact]
    public void A_photo_story_with_no_preview_is_unchanged()
    {
        // Older stories have no stripped bytes; they must still produce their usual sizes.
        var item = StoryHelper
            .ConvertToStoryItem(ExpiredPhotoStory(), OwnerId, photo: new FakePhotoReadModel())
            .ShouldBeOfType<TStoryItem>();

        var sizes = ((TPhoto)((TMessageMediaPhoto)item.Media).Photo).Sizes;
        sizes.OfType<TPhotoStrippedSize>().ShouldBeEmpty();
        sizes.Select(x => ((TPhotoSize)x).Type).ShouldBe(["m", "x", "y", "w"]);
    }
}
