using MongoDB.Driver;
using MyTelegram.ReadModel.Impl;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get a document by its SHA256 hash, mainly used for gifs
/// Possible errors
/// Code Type Description
/// 400 SHA256_HASH_INVALID The provided SHA256 hash is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDocumentByHash"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
///
/// <para>The lookup is by the <b>body</b> of the file, not by any id: a client that already has the bytes
/// asks whether this server knows them, so it can send the document instead of uploading it again. All
/// three arguments have to match — Telegram documents <c>sha256</c>, <c>size</c> and <c>mime_type</c>, and
/// matching on the hash alone would hand back a document of a different type for the same bytes.</para>
///
/// <para><c>Sha256</c> is only present on documents whose body this server itself held
/// (<c>GifDocumentPublisher</c>, the video renditions). Everything stored before that — stickers, custom
/// emoji, ringtones, themes, anything the file-server created — carries no hash and is never matched, so a
/// client asking about those bytes is told <c>documentEmpty</c> and uploads the file again. That is the
/// correct fallback: inventing a hash for a body we cannot read would make a client send a file the server
/// has no way to serve.</para>
/// </remarks>
internal sealed class GetDocumentByHashHandler(
    IMongoDatabase database,
    IObjectMapper objectMapper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetDocumentByHash, MyTelegram.Schema.IDocument>
{
    /// <summary>Length of a SHA-256 digest. Anything else cannot be one.</summary>
    private const int Sha256Length = 32;

    protected override async Task<MyTelegram.Schema.IDocument> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetDocumentByHash obj)
    {
        if (obj.Sha256.Length != Sha256Length)
        {
            RpcErrors.RpcErrors400.Sha256HashInvalid.ThrowRpcError();
        }

        if (obj.Size < 0 || string.IsNullOrEmpty(obj.MimeType))
        {
            return new TDocumentEmpty();
        }

        var hex = Convert.ToHexStringLower(obj.Sha256.Span);

        var document = await database
            .GetCollection<DocumentReadModel>("eventflow-documentreadmodel")
            .Find(Builders<DocumentReadModel>.Filter.And(
                Builders<DocumentReadModel>.Filter.Eq(p => p.Sha256, hex),
                Builders<DocumentReadModel>.Filter.Eq(p => p.Size, obj.Size),
                Builders<DocumentReadModel>.Filter.Eq(p => p.MimeType, obj.MimeType)))
            .FirstOrDefaultAsync();

        if (document == null)
        {
            return new TDocumentEmpty();
        }

        var result = objectMapper.Map<IDocumentReadModel, TDocument>(document);
        result.Thumbs ??= [];
        result.VideoThumbs ??= [];
        result.Attributes ??= [];

        return result;
    }
}
