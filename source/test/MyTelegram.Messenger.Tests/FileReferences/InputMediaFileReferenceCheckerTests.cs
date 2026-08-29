using MyTelegram.Messenger.Services;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.FileReferences;

/// <summary>
/// Feature: refusing an outgoing media constructor whose <c>file_reference</c> is not one this server
/// issued.
///
/// <para>Only <c>messages.sendMedia</c> and <c>messages.sendMultiMedia</c> are checked, because those are
/// the two methods whose documented error lists carry <c>FILE_REFERENCE_*</c> (measured against
/// corefork.telegram.org). The exact spelling of the error is the contract: tdlib reads the index out of
/// the digits after the prefix and Android only parses an index out of a name ending in
/// <c>_EXPIRED</c>.</para>
///
/// <para>See https://corefork.telegram.org/api/file-references</para>
/// </summary>
public class InputMediaFileReferenceCheckerTests
{
    private const long DocumentId = 5350513349223189212;
    private const long PhotoId = 5328746406449161431;
    private const long CoverId = 4242;

    [Fact]
    public void A_fresh_reference_is_accepted()
    {
        Should.NotThrow(() => Check(Document(Fresh(AccessHashType.Document, DocumentId))));
        Should.NotThrow(() => Check(Photo(Fresh(AccessHashType.Photo, PhotoId))));
    }

    [Fact]
    public void A_forged_reference_is_refused()
    {
        Refused(() => Check(Document(Tampered(AccessHashType.Document, DocumentId))))
            .ShouldBe("FILE_REFERENCE_INVALID");
        Refused(() => Check(Photo([])))
            .ShouldBe("FILE_REFERENCE_EMPTY");
    }

    /// <summary>
    /// The index names the album entry the client has to repair, so it must be the position in
    /// <c>multi_media</c> and not, say, the position among the entries that carry media.
    /// </summary>
    [Fact]
    public void An_album_entry_is_refused_by_its_position()
    {
        Refused(() => Check(Document(Tampered(AccessHashType.Document, DocumentId)), index: 0))
            .ShouldBe("FILE_REFERENCE_0_INVALID");
        Refused(() => Check(Document(Tampered(AccessHashType.Document, DocumentId)), index: 2))
            .ShouldBe("FILE_REFERENCE_2_INVALID");
    }

    /// <summary>
    /// A custom video cover carries a reference of its own, and clients repair it separately — Android
    /// gates that on the <c>COVER_EXPIRED</c> suffix, tdlib on a <c>COVER_</c> prefix after the index. A
    /// cover reported as if it were the document would make the client refetch the wrong thing.
    /// </summary>
    [Fact]
    public void A_video_cover_is_refused_as_a_cover()
    {
        var media = new TInputMediaDocument
        {
            Id = new TInputDocument
            {
                Id = DocumentId,
                AccessHash = 1,
                FileReference = Fresh(AccessHashType.Document, DocumentId)
            },
            VideoCover = new TInputPhoto
            {
                Id = CoverId,
                AccessHash = 1,
                FileReference = Tampered(AccessHashType.Photo, CoverId)
            }
        };

        Refused(() => Check(media)).ShouldBe("FILE_REFERENCE_COVER_INVALID");
        Refused(() => Check(media, index: 1)).ShouldBe("FILE_REFERENCE_1_COVER_INVALID");
    }

    /// <summary>
    /// "The same FILE_REFERENCE_%d_INVALID error may also be emitted by messages.sendMedia [...] when an
    /// inputMediaPaidMedia is provided with an array of extended_media": the index is a position in that
    /// array, so a single-media send can still produce an indexed error.
    /// </summary>
    [Fact]
    public void Paid_media_is_refused_by_its_position_in_extended_media()
    {
        var media = new TInputMediaPaidMedia
        {
            StarsAmount = 10,
            ExtendedMedia =
            [
                Document(Fresh(AccessHashType.Document, DocumentId)),
                Photo(Tampered(AccessHashType.Photo, PhotoId))
            ]
        };

        Refused(() => Check(media)).ShouldBe("FILE_REFERENCE_1_INVALID");
    }

    /// <summary>
    /// Media being uploaded carries no reference at all — there is nothing to check, and refusing it would
    /// make every fresh upload impossible.
    /// </summary>
    [Fact]
    public void Media_that_carries_no_reference_is_left_alone()
    {
        Should.NotThrow(() => Check(new TInputMediaUploadedPhoto
        {
            File = new TInputFile { Id = 1, Parts = 1, Name = "photo.jpg", Md5Checksum = string.Empty }
        }));
        Should.NotThrow(() => Check(new TInputMediaEmpty()));
        Should.NotThrow(() => Check(null));
    }

    private static void Check(IInputMedia? media, int? index = null)
    {
        InputMediaFileReferenceChecker.Check(TestFileReferences.Enforcing, media, index);
    }

    private static IInputMedia Document(byte[] reference) => new TInputMediaDocument
    {
        Id = new TInputDocument { Id = DocumentId, AccessHash = 1, FileReference = reference }
    };

    private static IInputMedia Photo(byte[] reference) => new TInputMediaPhoto
    {
        Id = new TInputPhoto { Id = PhotoId, AccessHash = 1, FileReference = reference }
    };

    private static byte[] Fresh(AccessHashType type, long id) => TestFileReferences.Enforcing.Create(type, id);

    private static byte[] Tampered(AccessHashType type, long id)
    {
        var reference = Fresh(type, id);
        reference[^1] ^= 0x01;
        return reference;
    }

    private static string Refused(Action action) => Should.Throw<RpcException>(action).RpcError.Message;
}
