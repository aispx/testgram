using MyTelegram.Converters;
using MyTelegram.Messenger.Services.Ringtones;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Ringtones;

/// <summary>
/// Feature: what may become a
/// <a href="https://corefork.telegram.org/api/ringtones">notification sound</a>, and where the sound a
/// client picked is stored.
///
/// <para>
/// The MIME table is a wire contract in both directions: the page names MP3 and OGG OPUS as the only
/// uploadable formats, and whether a stored sound is already MP3 is what decides between
/// <c>account.savedRingtone</c> and <c>account.savedRingtoneConverted</c>.
/// </para>
/// </summary>
public class RingtoneValidationTests
{
    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/mp3")]
    [InlineData("audio/ogg")]
    [InlineData("audio/opus")]
    [InlineData("AUDIO/MPEG")]
    public void The_documented_formats_may_be_uploaded(string mimeType)
    {
        RingtoneMimeTypes.IsUploadable(mimeType).ShouldBeTrue();
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    [InlineData("audio/m4a")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_RINGTONE_MIME_INVALID(string? mimeType)
    {
        RingtoneMimeTypes.IsUploadable(mimeType).ShouldBeFalse();
    }

    /// <summary>
    /// An already stored document may also be an <c>audio/m4a</c>, which is what Telegram Android is willing
    /// to play from its tone list (<c>RingtoneDataStore.ringtoneSupportedMimeType</c>) — it is only a fresh
    /// upload that is restricted to the two documented formats.
    /// </summary>
    [Theory]
    [InlineData("audio/m4a", true)]
    [InlineData("audio/mpeg3", true)]
    [InlineData("audio/ogg", true)]
    [InlineData("video/mp4", false)]
    public void A_stored_document_may_be_saved_from_a_wider_set(string mimeType, bool saveable)
    {
        RingtoneMimeTypes.IsSaveable(mimeType).ShouldBe(saveable);
    }

    [Theory]
    [InlineData("audio/mpeg", true)]
    [InlineData("audio/mp3", true)]
    [InlineData("audio/mpeg3", true)]
    [InlineData("audio/ogg", false)]
    [InlineData("audio/opus", false)]
    public void Only_an_mp3_needs_no_conversion(string mimeType, bool isMp3)
    {
        RingtoneMimeTypes.IsMp3(mimeType).ShouldBe(isMp3);
    }

    /// <summary>
    /// The two strings Telegram Android matches literally in <c>RingtoneUploader.error()</c>. Neither is in
    /// the method's documented error table, and neither is in the generated <c>RpcErrors</c>, so the spelling
    /// is only guarded here.
    /// </summary>
    [Fact]
    public void The_limit_errors_are_spelled_the_way_the_client_matches_them()
    {
        RingtoneExtraRpcErrors.RingtoneSizeTooBig.Message.ShouldBe("RINGTONE_SIZE_TOO_BIG");
        RingtoneExtraRpcErrors.RingtoneSizeTooBig.ErrorCode.ShouldBe(400);
        RingtoneExtraRpcErrors.RingtoneDurationTooLong.Message.ShouldBe("RINGTONE_DURATION_TOO_LONG");
        RingtoneExtraRpcErrors.RingtoneDurationTooLong.ErrorCode.ShouldBe(400);
    }

    [Fact]
    public void The_limits_fall_back_to_what_the_app_config_advertises()
    {
        // AppConfigHelper emits ringtone_size_max = 307200, ringtone_duration_max = 5 and
        // ringtone_saved_count_max = 100; refusing at a different number than the one the client was told
        // would produce an error message that contradicts itself.
        RingtoneLimits.SizeFallback.ShouldBe(307200);
        RingtoneLimits.DurationFallback.ShouldBe(5);
        RingtoneLimits.SavedCountFallback.ShouldBe(100);
    }
}

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/ringtones#setting-notification-sounds">setting a
/// notification sound</a> — the TL ↔ stored mapping, and which platform field the value lands in.
/// </summary>
public class NotificationSoundMappingTests
{
    [Fact]
    public void A_ringtone_keeps_its_document_id_in_both_directions()
    {
        var stored = NotificationSoundConverter.ToValue(new TNotificationSoundRingtone { Id = 5_432_100_001 });

        stored!.Kind.ShouldBe(NotificationSoundKind.Ringtone);
        stored.RingtoneId.ShouldBe(5_432_100_001);

        var tl = NotificationSoundConverter.ToTl(stored).ShouldBeOfType<TNotificationSoundRingtone>();
        tl.Id.ShouldBe(5_432_100_001);
    }

    [Fact]
    public void A_local_sound_keeps_its_title_and_payload()
    {
        var stored = NotificationSoundConverter.ToValue(new TNotificationSoundLocal
        {
            Title = "Chime",
            Data = "chime.mp3"
        });

        stored!.Kind.ShouldBe(NotificationSoundKind.Local);

        var tl = NotificationSoundConverter.ToTl(stored).ShouldBeOfType<TNotificationSoundLocal>();
        tl.Title.ShouldBe("Chime");
        tl.Data.ShouldBe("chime.mp3");
    }

    [Fact]
    public void Default_and_none_round_trip()
    {
        NotificationSoundConverter.ToTl(NotificationSoundConverter.ToValue(new TNotificationSoundDefault()))
            .ShouldBeOfType<TNotificationSoundDefault>();
        NotificationSoundConverter.ToTl(NotificationSoundConverter.ToValue(new TNotificationSoundNone()))
            .ShouldBeOfType<TNotificationSoundNone>();
    }

    /// <summary>
    /// An absent <c>sound</c> means "leave the current one alone", not "play the default": clients set only
    /// the fields they are changing, so reading absence as the default would clear a chosen sound on every
    /// mute.
    /// </summary>
    [Fact]
    public void An_absent_sound_is_not_the_default_sound()
    {
        NotificationSoundConverter.ToValue(null).ShouldBeNull();
        NotificationSoundConverter.ToTl(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(DeviceType.Android)]
    [InlineData(DeviceType.AndroidX)]
    public void An_android_session_writes_the_android_field(DeviceType deviceType)
    {
        var (ios, android, other) =
            NotificationSoundConverter.SplitByPlatform(NotificationSoundValue.Default, deviceType);

        ios.ShouldBeNull();
        android.ShouldNotBeNull();
        other.ShouldBeNull();
    }

    [Fact]
    public void An_ios_session_writes_the_ios_field()
    {
        var (ios, android, other) =
            NotificationSoundConverter.SplitByPlatform(NotificationSoundValue.Default, DeviceType.Ios);

        ios.ShouldNotBeNull();
        android.ShouldBeNull();
        other.ShouldBeNull();
    }

    /// <summary>
    /// TelegramCore reads <c>other_sound</c> whenever it is not building for iOS, so Telegram for macOS is a
    /// desktop client here, not an Apple one.
    /// </summary>
    [Theory]
    [InlineData(DeviceType.Desktop)]
    [InlineData(DeviceType.MacOs)]
    [InlineData(DeviceType.TdLib)]
    [InlineData(DeviceType.WebA)]
    [InlineData(DeviceType.WebK)]
    [InlineData(DeviceType.Unigram)]
    public void A_desktop_or_web_session_writes_the_other_field(DeviceType deviceType)
    {
        var (ios, android, other) =
            NotificationSoundConverter.SplitByPlatform(NotificationSoundValue.Default, deviceType);

        ios.ShouldBeNull();
        android.ShouldBeNull();
        other.ShouldNotBeNull();
    }

    [Fact]
    public void An_unknown_platform_writes_all_three_rather_than_losing_the_choice()
    {
        var (ios, android, other) =
            NotificationSoundConverter.SplitByPlatform(NotificationSoundValue.Default, DeviceType.Unknown);

        ios.ShouldNotBeNull();
        android.ShouldNotBeNull();
        other.ShouldNotBeNull();
    }

    [Fact]
    public void An_absent_sound_fills_no_platform_field()
    {
        NotificationSoundConverter.SplitByPlatform(null, DeviceType.Android).ShouldBe((null, null, null));
    }
}

/// <summary>
/// Feature: the <c>documentAttributeAudio</c> a saved notification sound is served with.
///
/// <para>
/// The document row belongs to the file server and is written from the attributes the upload carried, so on
/// a deployment where the messenger cannot see the staged parts the duration is only known after the row
/// exists. It is then kept in <c>saved_ringtones</c> and merged into the TL document on the way out — a tone
/// with no audio attribute shows no length in any client, and Telegram Android's <c>saveToRingtones</c> has
/// nothing to compare against <c>ringtone_duration_max</c>.
/// </para>
/// </summary>
public class RingtoneAudioAttributeTests
{
    private static TDocument Document(params IDocumentAttribute[] attributes) =>
        new() { Attributes = new TVector<IDocumentAttribute>(attributes) };

    [Fact]
    public void A_probed_duration_is_added_to_a_document_that_has_none()
    {
        var document = RingtoneAudioAttribute.Merge(
            Document(new TDocumentAttributeFilename { FileName = "tone.mp3" }), 3, "Chime", "Someone");

        var audio = document.Attributes.OfType<TDocumentAttributeAudio>().ShouldHaveSingleItem();
        audio.Duration.ShouldBe(3);
        audio.Title.ShouldBe("Chime");
        audio.Performer.ShouldBe("Someone");
        audio.Voice.ShouldBeFalse();
        document.Attributes.OfType<TDocumentAttributeFilename>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// An attribute that is already there came from the file server and describes the real body, so it wins.
    /// </summary>
    [Fact]
    public void An_existing_audio_attribute_is_left_alone()
    {
        var document = RingtoneAudioAttribute.Merge(
            Document(new TDocumentAttributeAudio { Duration = 7, Title = "Original" }), 3, "Chime", null);

        var audio = document.Attributes.OfType<TDocumentAttributeAudio>().ShouldHaveSingleItem();
        audio.Duration.ShouldBe(7);
        audio.Title.ShouldBe("Original");
    }

    /// <summary>
    /// With no ffprobe the duration is unknown, and a made-up one is worse than none.
    /// </summary>
    [Fact]
    public void An_unknown_duration_adds_nothing()
    {
        var document = RingtoneAudioAttribute.Merge(Document(), 0, null, null);

        document.Attributes.OfType<TDocumentAttributeAudio>().ShouldBeEmpty();
    }

    [Fact]
    public void The_stored_row_is_the_source_of_the_attribute()
    {
        var document = RingtoneAudioAttribute.Merge(Document(), new SavedRingtoneDocument
        {
            DurationSeconds = 4,
            Title = "Bell",
            Performer = "Server"
        });

        var audio = document.Attributes.OfType<TDocumentAttributeAudio>().ShouldHaveSingleItem();
        audio.Duration.ShouldBe(4);
        audio.Title.ShouldBe("Bell");
        audio.Performer.ShouldBe("Server");
    }
}
