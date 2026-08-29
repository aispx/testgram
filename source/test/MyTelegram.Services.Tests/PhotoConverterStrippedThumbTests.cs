using MyTelegram.Converters.TLObjects.LatestLayer;
using MyTelegram;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Services.Tests;

public class PhotoConverterStrippedThumbTests
{
    [Fact]
    public void ToProfilePhoto_UsesValidStrippedThumbFromSizes2()
    {
        var strippedThumb = new byte[] { 1, 24, 24, 0x42 };
        var converter = new PhotoConverter(TestFileReferences.Helper);

        var profilePhoto = converter.ToProfilePhoto(new PhotoReadModelStub
        {
            PhotoId = 123,
            DcId = 2,
            Sizes2 = [new TPhotoStrippedSize { Type = "i", Bytes = strippedThumb }]
        });

        var userProfilePhoto = profilePhoto.ShouldBeOfType<TUserProfilePhoto>();
        userProfilePhoto.StrippedThumb.HasValue.ShouldBeTrue();
        userProfilePhoto.StrippedThumb.Value.ToArray().ShouldBe(strippedThumb);
    }

    [Fact]
    public void ToProfilePhoto_DropsEmptyStrippedThumbs()
    {
        var converter = new PhotoConverter(TestFileReferences.Helper);

        var profilePhoto = converter.ToProfilePhoto(new PhotoReadModelStub
        {
            PhotoId = 123,
            DcId = 2,
            Sizes2 = [new TPhotoStrippedSize { Type = "i", Bytes = ReadOnlyMemory<byte>.Empty }],
            Sizes = [new PhotoSize(0, 0, 0, "i", [])]
        });

        var userProfilePhoto = profilePhoto.ShouldBeOfType<TUserProfilePhoto>();
        userProfilePhoto.StrippedThumb.HasValue.ShouldBeFalse();
    }

    private sealed class PhotoReadModelStub : IPhotoReadModel
    {
        public string Id { get; init; } = "photo-123";
        public long AccessHash { get; init; }
        public int Date { get; init; }
        public int DcId { get; init; }
        public byte[] FileReference { get; init; } = [];
        public bool HasStickers { get; init; }
        public bool HasVideo { get; init; }
        public long PhotoId { get; init; }
        public long Size { get; init; }
        public List<PhotoSize>? Sizes { get; init; }
        public long UserId { get; init; }
        public List<VideoSize>? VideoSizes { get; init; }
        public bool IsProfilePhoto { get; init; }
        public List<IPhotoSize>? Sizes2 { get; init; }
        public List<IVideoSize>? VideoSizes2 { get; init; }
    }
}
