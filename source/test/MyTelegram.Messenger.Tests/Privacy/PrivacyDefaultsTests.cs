using MyTelegram.Messenger.Handlers.LatestLayer.Messages;
using MyTelegram.Messenger.Services.Privacy;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Privacy;

/// <summary>
/// Covers the defaults reported for unconfigured privacy keys and the voice-note detection
/// that gates <c>privacyKeyVoiceMessages</c>.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
public class PrivacyDefaultsTests
{
    [Fact]
    public void PhoneNumberShouldDefaultToContacts()
    {
        PrivacyService.GetDefaultRule(PrivacyType.PhoneNumber)
            .ShouldBeOfType<TPrivacyValueAllowContacts>();
    }

    [Theory]
    [InlineData(PrivacyType.StatusTimestamp)]
    [InlineData(PrivacyType.ProfilePhoto)]
    [InlineData(PrivacyType.About)]
    [InlineData(PrivacyType.Birthday)]
    [InlineData(PrivacyType.Forwards)]
    [InlineData(PrivacyType.VoiceMessages)]
    [InlineData(PrivacyType.SavedMusic)]
    public void OtherKeysShouldDefaultToEverybody(PrivacyType type)
    {
        PrivacyService.GetDefaultRule(type).ShouldBeOfType<TPrivacyValueAllowAll>();
    }

    [Fact]
    public void ShouldTreatVoiceFlaggedAudioAsVoiceNote()
    {
        var media = UploadedDocument(new TDocumentAttributeAudio { Voice = true });

        VoiceMessageHelper.IsVoiceMedia(media).ShouldBeTrue();
    }

    [Fact]
    public void ShouldNotTreatMusicAsVoiceNote()
    {
        // Regression: the old check matched any audio attribute, so sending ordinary music to
        // someone who merely disallowed voice messages was rejected.
        var media = UploadedDocument(new TDocumentAttributeAudio { Voice = false });

        VoiceMessageHelper.IsVoiceMedia(media).ShouldBeFalse();
    }

    [Fact]
    public void ShouldNotTreatNonAudioDocumentAsVoiceNote()
    {
        var media = UploadedDocument(new TDocumentAttributeFilename { FileName = "a.pdf" });

        VoiceMessageHelper.IsVoiceMedia(media).ShouldBeFalse();
    }

    [Fact]
    public void ShouldDetectVoiceNoteInsideAlbum()
    {
        var album = new List<IInputSingleMedia>
        {
            new TInputSingleMedia { Media = UploadedDocument(new TDocumentAttributeFilename { FileName = "a.pdf" }) },
            new TInputSingleMedia { Media = UploadedDocument(new TDocumentAttributeAudio { Voice = true }) }
        };

        VoiceMessageHelper.ContainsVoiceMedia(album).ShouldBeTrue();
    }

    [Fact]
    public void ShouldReportNoVoiceNoteForAlbumWithoutOne()
    {
        var album = new List<IInputSingleMedia>
        {
            new TInputSingleMedia { Media = UploadedDocument(new TDocumentAttributeAudio { Voice = false }) }
        };

        VoiceMessageHelper.ContainsVoiceMedia(album).ShouldBeFalse();
    }

    [Fact]
    public void ShouldHandleEmptyAlbum()
    {
        VoiceMessageHelper.ContainsVoiceMedia(null).ShouldBeFalse();
        VoiceMessageHelper.ContainsVoiceMedia([]).ShouldBeFalse();
    }

    private static IInputMedia UploadedDocument(params IDocumentAttribute[] attributes)
    {
        return new TInputMediaUploadedDocument
        {
            File = new TInputFile(),
            MimeType = "audio/ogg",
            Attributes = new TVector<IDocumentAttribute>(attributes)
        };
    }
}
