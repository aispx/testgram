namespace MyTelegram.Schema;

public class ProfilePhotoStrippedThumbTests
{
    [Fact]
    public void UserProfilePhoto_DoesNotSerializeEmptyStrippedThumb()
    {
        var photo = new TUserProfilePhoto
        {
            Flags = 1 << 1,
            PhotoId = 123,
            DcId = 2,
            StrippedThumb = ReadOnlyMemory<byte>.Empty
        };

        var roundTripped = photo.ToBytes().AsMemory().ToTObject<TUserProfilePhoto>();

        roundTripped.Flags.IsBitSet(1).ShouldBeFalse();
        roundTripped.StrippedThumb.HasValue.ShouldBeFalse();
        roundTripped.PhotoId.ShouldBe(123);
        roundTripped.DcId.ShouldBe(2);
    }

    [Fact]
    public void UserProfilePhoto_SerializesValidStrippedThumb()
    {
        var strippedThumb = new byte[] { 1, 24, 24, 0x42 };
        var photo = new TUserProfilePhoto
        {
            PhotoId = 123,
            DcId = 2,
            StrippedThumb = strippedThumb
        };

        var roundTripped = photo.ToBytes().AsMemory().ToTObject<TUserProfilePhoto>();

        roundTripped.Flags.IsBitSet(1).ShouldBeTrue();
        roundTripped.StrippedThumb.HasValue.ShouldBeTrue();
        roundTripped.StrippedThumb.Value.ToArray().ShouldBe(strippedThumb);
    }

    [Fact]
    public void ChatPhoto_DoesNotSerializeEmptyStrippedThumb()
    {
        var photo = new TChatPhoto
        {
            Flags = 1 << 1,
            PhotoId = 456,
            DcId = 2,
            StrippedThumb = ReadOnlyMemory<byte>.Empty
        };

        var roundTripped = photo.ToBytes().AsMemory().ToTObject<TChatPhoto>();

        roundTripped.Flags.IsBitSet(1).ShouldBeFalse();
        roundTripped.StrippedThumb.HasValue.ShouldBeFalse();
        roundTripped.PhotoId.ShouldBe(456);
        roundTripped.DcId.ShouldBe(2);
    }
}
