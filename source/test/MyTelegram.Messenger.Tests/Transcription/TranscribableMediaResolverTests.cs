using Moq;
using MyTelegram.Messenger.Services.Transcription;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: which messages <a href="https://corefork.telegram.org/api/transcribe">messages.transcribeAudio</a>
/// accepts.
///
/// <para>
/// TDLib's <c>can_recognize_message_speech</c> accepts exactly two content types — <c>VoiceNote</c> and
/// <c>VideoNote</c> — Android offers the button for <c>isVoice() || isRoundVideo()</c> and tdesktop marks
/// the entry <c>roundview</c> when <c>document->isVideoMessage()</c>. Both halves matter in opposite
/// directions: dropping round videos refuses something every client offers, and accepting any audio
/// attachment spends a free-trial try on a music track whose result no client will render.
/// </para>
/// </summary>
public class TranscribableMediaResolverTests
{
    private const long DocumentId = 5_204_474_871_112_567_500;

    [Fact]
    public void A_voice_note_is_transcribable_and_carries_its_duration()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Voice = true,
            Document = Document(new TDocumentAttributeAudio { Voice = true, Duration = 17 })
        }));

        media.ShouldNotBeNull();
        media.DocumentId.ShouldBe(DocumentId);
        media.DurationSeconds.ShouldBe(17);
        media.IsRoundVideo.ShouldBeFalse();
    }

    /// <summary>
    /// A round video note is a video file, and only its audio track is of interest. The duration lives on
    /// <c>documentAttributeVideo</c> as a double there, and it is the number compared against the
    /// advertised <c>transcribe_audio_trial_duration_max</c>, so it is rounded rather than truncated.
    /// </summary>
    [Fact]
    public void A_round_video_note_is_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Round = true,
            Document = Document(new TDocumentAttributeVideo { RoundMessage = true, Duration = 8.6, W = 384, H = 384 })
        }));

        media.ShouldNotBeNull();
        media.DurationSeconds.ShouldBe(9);
        media.IsRoundVideo.ShouldBeTrue();
    }

    /// <summary>
    /// The distinction the whole resolver exists for: a music file carries a
    /// <c>documentAttributeAudio</c> too, and only the <c>voice</c> flag separates it from a voice note.
    /// </summary>
    [Fact]
    public void An_ordinary_music_file_is_not_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Document = Document(new TDocumentAttributeAudio
            {
                Voice = false,
                Duration = 240,
                Title = "Some song",
                Performer = "Some band"
            })
        }));

        media.ShouldBeNull();
    }

    [Fact]
    public void A_video_that_is_not_a_round_message_is_not_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Video = true,
            Document = Document(new TDocumentAttributeVideo { Duration = 30, W = 1280, H = 720 })
        }));

        media.ShouldBeNull();
    }

    [Fact]
    public void A_sticker_is_not_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Document = Document(new TDocumentAttributeSticker { Alt = "🙂", Stickerset = new TInputStickerSetEmpty() })
        }));

        media.ShouldBeNull();
    }

    [Fact]
    public void A_photo_is_not_transcribable()
    {
        TranscribableMediaResolver.Resolve(Message(new TMessageMediaPhoto())).ShouldBeNull();
    }

    [Fact]
    public void A_text_message_is_not_transcribable()
    {
        TranscribableMediaResolver.Resolve(Message(null)).ShouldBeNull();
    }

    /// <summary>
    /// The stored media may carry the <c>voice</c> flag without the attribute, which is what the read model
    /// looks like for messages written by some of the send paths. It is still a voice note; there is simply
    /// no duration to enforce a limit with, and inventing one would be worse than having none.
    /// </summary>
    [Fact]
    public void A_voice_flag_without_the_attribute_is_still_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Voice = true,
            Document = Document()
        }));

        media.ShouldNotBeNull();
        media.DurationSeconds.ShouldBe(0);
    }

    /// <summary>A document with no id names no body, so there is nothing to recognise.</summary>
    [Fact]
    public void A_document_without_an_id_is_not_transcribable()
    {
        var media = TranscribableMediaResolver.Resolve(Message(new TMessageMediaDocument
        {
            Voice = true,
            Document = new TDocument
            {
                Id = 0,
                MimeType = "audio/ogg",
                Attributes = new TVector<IDocumentAttribute>(new TDocumentAttributeAudio
                {
                    Voice = true,
                    Duration = 5
                })
            }
        }));

        media.ShouldBeNull();
    }

    private static TDocument Document(params IDocumentAttribute[] attributes)
    {
        return new TDocument
        {
            Id = DocumentId,
            AccessHash = 42,
            MimeType = "audio/ogg",
            Size = 4096,
            DcId = 2,
            Attributes = new TVector<IDocumentAttribute>(attributes)
        };
    }

    private static IMessageReadModel Message(IMessageMedia? media)
    {
        var message = new Mock<IMessageReadModel>(MockBehavior.Loose);
        message.SetupGet(p => p.Media2).Returns(media);

        return message.Object;
    }
}
