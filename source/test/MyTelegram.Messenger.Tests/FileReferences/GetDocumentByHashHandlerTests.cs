using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Core;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using System.Reflection;

namespace MyTelegram.Messenger.Tests.FileReferences;

/// <summary>
/// Feature: <c>messages.getDocumentByHash</c>.
///
/// <para>
/// The method answers "do you already have these bytes?", so a client that holds a file can send it by id
/// instead of uploading it again. It threw <c>NotImplementedException</c> here, which every client reads as
/// a hard error rather than as "not on the server" — the honest answer for an unknown body is
/// <c>documentEmpty</c>.
/// </para>
///
/// <para>
/// All three arguments have to match. Telegram documents <c>sha256</c>, <c>size</c> and <c>mime_type</c>,
/// and matching the hash alone would hand back a document of a different type or length for the same
/// bytes — the client would then send a file that does not describe what it has.
/// </para>
///
/// <para>See https://corefork.telegram.org/method/messages.getDocumentByHash</para>
/// </summary>
public class GetDocumentByHashHandlerTests
{
    private const long DocumentId = 5350513349223189212;
    private const string MimeType = "video/mp4";
    private const long Size = 4096;

    private static readonly byte[] Sha256 = Convert.FromHexString(
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");

    /// <summary>A digest is 32 bytes; anything else cannot be one.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task A_hash_of_the_wrong_length_is_refused(int length)
    {
        using var mongo = EmbeddedMongoServer.Start();

        var error = await Should.ThrowAsync<RpcException>(() =>
            InvokeAsync(mongo.Database, new byte[length], Size, MimeType));

        error.RpcError.Message.ShouldBe("SHA256_HASH_INVALID");
    }

    [RequiresMongoDbFact]
    public async Task A_known_body_yields_its_document()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertAsync(mongo.Database);

        var result = await InvokeAsync(mongo.Database, Sha256, Size, MimeType);

        result.ShouldBeOfType<TDocument>().Id.ShouldBe(DocumentId);
    }

    /// <summary>
    /// "Not here, upload it" is a successful answer, not an error: a client that gets an error cannot fall
    /// back to uploading.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_unknown_body_is_documentEmpty()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertAsync(mongo.Database);

        var other = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000001");

        (await InvokeAsync(mongo.Database, other, Size, MimeType)).ShouldBeOfType<TDocumentEmpty>();
    }

    /// <summary>
    /// The size and the mime type are part of the identity, so a hash that matches on its own is not a
    /// match.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_size_and_mime_type_have_to_match_too()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await InsertAsync(mongo.Database);

        (await InvokeAsync(mongo.Database, Sha256, Size + 1, MimeType)).ShouldBeOfType<TDocumentEmpty>();
        (await InvokeAsync(mongo.Database, Sha256, Size, "image/jpeg")).ShouldBeOfType<TDocumentEmpty>();
    }

    /// <summary>
    /// A document whose body this server never held carries no <c>Sha256</c>, and a client asking about
    /// those bytes must not be handed an unrelated row.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_document_without_a_recorded_hash_is_never_matched()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await mongo.Database.GetCollection<BsonDocument>("eventflow-documentreadmodel").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = $"documentreadmodel-{DocumentId}",
                ["DocumentId"] = DocumentId,
                ["AccessHash"] = 1L,
                ["Date"] = 1775290238,
                ["MimeType"] = MimeType,
                ["Size"] = Size
            });

        (await InvokeAsync(mongo.Database, Sha256, Size, MimeType)).ShouldBeOfType<TDocumentEmpty>();
    }

    private static Task InsertAsync(IMongoDatabase database)
    {
        return database.GetCollection<BsonDocument>("eventflow-documentreadmodel").InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = $"documentreadmodel-{DocumentId}",
                ["DocumentId"] = DocumentId,
                ["AccessHash"] = 1L,
                ["Date"] = 1775290238,
                ["DcId"] = 2,
                ["MimeType"] = MimeType,
                ["Size"] = Size,
                ["Sha256"] = Convert.ToHexStringLower(Sha256)
            });
    }

    private static async Task<MyTelegram.Schema.IDocument> InvokeAsync(IMongoDatabase database,
        byte[] sha256, long size, string mimeType)
    {
        // The real mapper needs the whole layered-converter graph; what is under test here is the lookup
        // and the shape of the answer, so the mapping itself is stood in for.
        var mapper = new Mock<IObjectMapper>();
        mapper.Setup(p => p.Map<IDocumentReadModel, TDocument>(It.IsAny<IDocumentReadModel>()))
            .Returns<IDocumentReadModel>(source => new TDocument
            {
                Id = source.DocumentId,
                AccessHash = source.AccessHash,
                MimeType = source.MimeType,
                Size = source.Size,
                DcId = source.DcId
            });

        var handlerType = typeof(MyTelegram.Messenger.MyTelegramMessengerServerOptions).Assembly
            .GetType("MyTelegram.Messenger.Handlers.LatestLayer.Messages.GetDocumentByHashHandler",
                throwOnError: true)!;
        var handler = Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [database, mapper.Object],
            culture: null)!;

        var method = handlerType.GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(2010001);

        var request = new MyTelegram.Schema.Messages.RequestGetDocumentByHash
        {
            Sha256 = sha256,
            Size = size,
            MimeType = mimeType
        };

        var task = (Task<MyTelegram.Schema.IDocument>)method.Invoke(handler, [input.Object, request])!;

        return await task;
    }
}
