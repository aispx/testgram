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

        // With MediaSize = 0 the guess claims a flat 100000-byte image that does not exist at that
        // length; the client requests a range past the end of the file and renders nothing.
        var guessedSizes = ((TPhoto)((TMessageMediaPhoto)guessed.Media).Photo).Sizes;
        guessedSizes.Select(s => ((TPhotoSize)s).Size).ShouldContain(100000);

        var realSizes = ((TPhoto)((TMessageMediaPhoto)real.Media).Photo).Sizes;
        realSizes.Select(s => ((TPhotoSize)s).Size).ShouldNotContain(100000);
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
}
