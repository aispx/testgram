using System.Collections;

namespace MyTelegram.Services.Services;

/// <summary>
/// Stamps a current <c>file_reference</c> onto every <c>document</c> and <c>photo</c> leaving this
/// server, whatever built it.
///
/// <para>Doing it once on the way out rather than in each of the forty-odd places that construct a
/// <c>TDocument</c> is the only way to be sure none is missed, and a missed one is not a cosmetic
/// defect: with checking enforced, a document served with a stale or empty reference is media no
/// client can download and no client can repair. The same reasoning already put
/// <see cref="DcIdNormalizer"/> on this path.</para>
///
/// <para>The walk reuses the generated <see cref="IAccessHashOwner"/> graph — the one
/// <see cref="QueuedObjectMessageSender"/> uses to rewrite outbound access hashes — because it already
/// reaches every nested media object (<c>updates -> message -> messageMediaDocument -> document</c>)
/// and is generated from the schema, so a new response type cannot quietly fall outside it.</para>
///
/// <para>See https://corefork.telegram.org/api/file-references</para>
/// </summary>
public sealed class FileReferenceStamper(IFileReferenceHelper fileReferenceHelper) : IFileReferenceStamper,
    ISingletonDependency
{
    private const int MaxDepth = 32;

    public void Stamp(object? root)
    {
        if (root == null)
        {
            return;
        }

        try
        {
            Walk(root, 0);
        }
        catch
        {
            // A response must never fail because of this. A reference that was not stamped is refused
            // later with FILE_REFERENCE_INVALID, which clients recover from; a thrown exception here
            // would lose the whole answer.
        }
    }

    private void Walk(object? node, int depth)
    {
        if (node == null || depth > MaxDepth || node is string || node is byte[])
        {
            return;
        }

        switch (node)
        {
            case TDocument document:
                document.FileReference = fileReferenceHelper.Create(AccessHashType.Document, document.Id);
                break;
            case TPhoto photo:
                photo.FileReference = fileReferenceHelper.Create(AccessHashType.Photo, photo.Id);
                break;
        }

        if (node is IAccessHashOwner owner)
        {
            foreach (var item in owner.GetAccessHashes())
            {
                Walk(item, depth + 1);
            }
        }

        // TVector<T> is both an IObject and a list, so a vector of media has to be walked as a list
        // even when it also matched above.
        if (node is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Walk(item, depth + 1);
            }
        }
    }
}
