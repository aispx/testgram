using Moq;
using MyTelegram.Messenger.Services.Gifs;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Gifs;

/// <summary>
/// Feature: what counts as a GIF, per <a href="https://corefork.telegram.org/api/gifs">api/gifs</a>.
///
/// <para>
/// "On Telegram, GIFs are actually MPEG4 videos without sound." Both halves are load-bearing: tdlib
/// refuses to save anything whose mime type is not <c>video/mp4</c>, and tdesktop drops documents that
/// are not <c>isGifv()</c> out of the saved list it receives — which silently shortens its list relative
/// to the server's and breaks the hash for good.
/// </para>
/// </summary>
public class GifDocumentHelperTests
{
    [Fact]
    public void An_mp4_with_the_animated_attribute_is_a_gif()
    {
        GifDocumentHelper.IsAnimatedMp4(ReadModel("video/mp4", animated: true)).ShouldBeTrue();
    }

    [Fact]
    public void A_raw_gif_upload_is_not_a_gif_yet()
    {
        // The case the server has to convert: animated, but not MPEG4.
        var document = ReadModel("image/gif", animated: true);

        GifDocumentHelper.IsAnimatedMp4(document).ShouldBeFalse();
        GifDocumentHelper.HasAnimatedAttribute(document).ShouldBeTrue();
    }

    [Fact]
    public void An_mp4_without_the_animated_attribute_is_an_ordinary_video()
    {
        GifDocumentHelper.IsAnimatedMp4(ReadModel("video/mp4", animated: false)).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_document_is_not_a_gif()
    {
        GifDocumentHelper.IsAnimatedMp4((IDocumentReadModel?)null).ShouldBeFalse();
        GifDocumentHelper.IsAnimatedMp4((TDocument?)null).ShouldBeFalse();
    }

    [Fact]
    public void The_mime_type_comparison_is_case_insensitive()
    {
        GifDocumentHelper.IsAnimatedMp4(ReadModel("VIDEO/MP4", animated: true)).ShouldBeTrue();
    }

    [Fact]
    public void Only_a_non_mp4_animation_needs_converting()
    {
        GifDocumentHelper.NeedsMp4Conversion(Document("image/gif", animated: true)).ShouldBeTrue();
        GifDocumentHelper.NeedsMp4Conversion(Document("video/mp4", animated: true)).ShouldBeFalse();
        GifDocumentHelper.NeedsMp4Conversion(Document("video/mp4", animated: false)).ShouldBeFalse();
        GifDocumentHelper.NeedsMp4Conversion(null).ShouldBeFalse();
    }

    [Fact]
    public void The_document_of_a_media_is_only_read_for_document_media()
    {
        var document = Document("video/mp4", animated: true);

        GifDocumentHelper.GetDocument(new TMessageMediaDocument { Document = document }).ShouldBe(document);
        GifDocumentHelper.GetDocument(new TMessageMediaEmpty()).ShouldBeNull();
        GifDocumentHelper.GetDocument(null).ShouldBeNull();
    }

    private static IDocumentReadModel ReadModel(string mimeType, bool animated)
    {
        var document = new Mock<IDocumentReadModel>(MockBehavior.Loose);
        document.SetupGet(p => p.DocumentId).Returns(4242);
        document.SetupGet(p => p.MimeType).Returns(mimeType);
        document.SetupGet(p => p.Attributes2).Returns(animated
            ? [new TDocumentAttributeAnimated(), new TDocumentAttributeFilename { FileName = "a.mp4" }]
            : [new TDocumentAttributeVideo { W = 1, H = 1, Duration = 1 }]);

        return document.Object;
    }

    private static TDocument Document(string mimeType, bool animated)
    {
        return new TDocument
        {
            Id = 4242,
            MimeType = mimeType,
            Attributes = animated
                ? new TVector<IDocumentAttribute>(new TDocumentAttributeAnimated())
                : new TVector<IDocumentAttribute>(new TDocumentAttributeVideo { W = 1, H = 1, Duration = 1 })
        };
    }
}
