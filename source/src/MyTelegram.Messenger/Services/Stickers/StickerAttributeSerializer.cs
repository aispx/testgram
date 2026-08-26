using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace MyTelegram.Messenger.Services.Stickers;

/// <summary>
/// Reads and writes the <c>Attributes2</c> field of a document read model.
///
/// <para>The field is a BSON <b>array</b> of attribute documents, each tagged with a <c>_t</c>
/// discriminator holding the short type name — the shape <c>scripts/seed_stickers.py</c> writes and the
/// only shape the read path can deserialize. Serialising the vector with
/// <c>System.Text.Json</c> and feeding the result to <c>BsonSerializer.Deserialize&lt;BsonDocument&gt;</c>,
/// as the sticker-creation handler used to, cannot work: a vector serialises to a JSON array and that
/// call demands an object.</para>
/// </summary>
public static class StickerAttributeSerializer
{
    public static List<IDocumentAttribute> Read(BsonDocument documentRow)
    {
        if (!documentRow.TryGetValue("Attributes2", out var value) || value.IsBsonNull)
        {
            return [];
        }

        try
        {
            var normalized = value.DeepClone();
            NormalizeIdElements(normalized);

            return [..BsonSerializer.Deserialize<TVector<IDocumentAttribute>>(normalized.ToJson())];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Renames a nested <c>Id</c> element to <c>_id</c>.
    ///
    /// <para>The MongoDB driver's automapper maps any member called <c>Id</c> to the BSON element
    /// <c>_id</c>, so that is how it writes — and reads — a nested TL object such as
    /// <c>documentAttributeSticker.stickerset</c>. The seeder scripts wrote the literal member name
    /// instead, and the mismatch is silent: the attribute deserializes, but with
    /// <c>inputStickerSetID.id = 0</c>. Inside a stickerset response that went unnoticed, because the set
    /// being returned overwrites the field anyway; in the flat lists (recent, favourites, emoji
    /// suggestions) the stored value is what goes out, and a zero id is a sticker whose pack cannot be
    /// opened.</para>
    ///
    /// <para>Accepting both shapes here means no migration is needed for the rows already seeded. The
    /// scripts write <c>_id</c> from now on.</para>
    /// </summary>
    private static void NormalizeIdElements(BsonValue value)
    {
        switch (value)
        {
            case BsonArray array:
                foreach (var item in array)
                {
                    NormalizeIdElements(item);
                }

                break;

            case BsonDocument document:
            {
                // Only TL objects, identified by their discriminator — never a read model root, whose own
                // Id genuinely is the _id.
                if (document.Contains("_t") && document.Contains("Id") && !document.Contains("_id"))
                {
                    var id = document["Id"];
                    document.Remove("Id");
                    document["_id"] = id;
                }

                foreach (var element in document.Elements.ToList())
                {
                    NormalizeIdElements(element.Value);
                }

                break;
            }
        }
    }

    /// <summary>
    /// The value to store back. The nominal type is the interface, which is what makes the driver emit the
    /// <c>_t</c> discriminator each attribute needs to be read back as the right constructor.
    /// </summary>
    public static BsonArray Write(IEnumerable<IDocumentAttribute> attributes)
    {
        return new BsonArray(attributes.Select(p => (BsonValue)p.ToBsonDocument<IDocumentAttribute>()));
    }

    /// <summary>
    /// Replaces the sticker classification of a document while keeping everything else — image size, video
    /// duration and so on — that a set edit has no business touching.
    /// </summary>
    public static BsonArray WithPrimaryAttribute(BsonDocument documentRow, IDocumentAttribute primaryAttribute)
    {
        var attributes = Read(documentRow)
            .Where(p => p is not TDocumentAttributeSticker and not TDocumentAttributeCustomEmoji)
            .ToList();

        attributes.Insert(0, primaryAttribute);

        return Write(attributes);
    }
}
