using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Services;

/// <summary>
/// The single point at which a <c>file_reference</c> is put on the wire.
///
/// <para>Forty-odd places in this repository build a <c>TDocument</c> by hand. Stamping the reference on
/// the way out instead of in each of them is what makes it impossible to miss one — and a missed one is
/// media that no client can download and no client can repair, because the reference it was given never
/// validates. These tests pin the reach of the walk, not the value it writes.</para>
///
/// <para>See https://corefork.telegram.org/api/file-references</para>
/// </summary>
public class FileReferenceStamperTests
{
    private const long DocumentId = 5350513349223189212;
    private const long PhotoId = 5328746406449161431;

    [Fact]
    public void A_bare_document_is_stamped()
    {
        var document = new TDocument { Id = DocumentId, Attributes = [] };

        CreateSut().Stamp(document);

        Validate(document.FileReference.ToArray(), AccessHashType.Document, DocumentId)
            .ShouldBe(FileReferenceState.Valid);
    }

    [Fact]
    public void A_bare_photo_is_stamped()
    {
        var photo = new TPhoto { Id = PhotoId, Sizes = [] };

        CreateSut().Stamp(photo);

        Validate(photo.FileReference, AccessHashType.Photo, PhotoId).ShouldBe(FileReferenceState.Valid);
    }

    /// <summary>
    /// The shape that matters most: media three levels down inside an update, which is how every message
    /// with a file reaches a client.
    /// </summary>
    [Fact]
    public void Media_nested_inside_an_update_is_stamped()
    {
        var document = new TDocument { Id = DocumentId, Attributes = [] };
        var updates = new TUpdates
        {
            Updates =
            [
                new TUpdateNewMessage
                {
                    Message = new TMessage
                    {
                        Id = 1,
                        PeerId = new TPeerUser { UserId = 2010001 },
                        Message = string.Empty,
                        Media = new TMessageMediaDocument { Document = document }
                    }
                }
            ],
            Users = [],
            Chats = [],
            Date = 0,
            Seq = 0
        };

        CreateSut().Stamp(updates);

        Validate(document.FileReference.ToArray(), AccessHashType.Document, DocumentId)
            .ShouldBe(FileReferenceState.Valid);
    }

    /// <summary>
    /// A vector of media is the other common shape — <c>messages.stickerSet.documents</c>,
    /// <c>account.savedRingtones.ringtones</c>, <c>messages.availableEffects.documents</c>.
    /// </summary>
    [Fact]
    public void Every_document_of_a_vector_is_stamped()
    {
        var documents = new TVector<IDocument>(
            new TDocument { Id = 1, Attributes = [] },
            new TDocument { Id = 2, Attributes = [] },
            new TDocument { Id = 3, Attributes = [] });

        CreateSut().Stamp(documents);

        foreach (var document in documents.Cast<TDocument>())
        {
            Validate(document.FileReference.ToArray(), AccessHashType.Document, document.Id)
                .ShouldBe(FileReferenceState.Valid);
        }
    }

    /// <summary>
    /// A reference the caller had already put on the object is replaced, not kept: the stored values this
    /// server used to serve were random constants that can never validate.
    /// </summary>
    [Fact]
    public void A_stale_reference_is_replaced()
    {
        var document = new TDocument { Id = DocumentId, Attributes = [], FileReference = new byte[16] };

        CreateSut().Stamp(document);

        Validate(document.FileReference.ToArray(), AccessHashType.Document, DocumentId)
            .ShouldBe(FileReferenceState.Valid);
    }

    [Fact]
    public void An_object_with_no_media_is_left_alone()
    {
        var sut = CreateSut();

        Should.NotThrow(() => sut.Stamp(new TBoolTrue()));
        Should.NotThrow(() => sut.Stamp(null));
    }

    private static FileReferenceState Validate(byte[] reference, AccessHashType type, long id)
    {
        return Helper.Validate(reference, type, id);
    }

    private static FileReferenceHelper Helper { get; } = Create();

    private static FileReferenceStamper CreateSut() => new(Helper);

    private static FileReferenceHelper Create()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:FileReferences:SecretKey"] = "test-secret-key"
            })
            .Build();

        return new FileReferenceHelper(configuration, NullLogger<FileReferenceHelper>.Instance);
    }
}
