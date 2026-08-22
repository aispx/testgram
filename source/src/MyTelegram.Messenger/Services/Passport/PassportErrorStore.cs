using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Mongo-backed <see cref="IPassportErrorStore"/>. Errors are decomposed into their fields rather than
/// stored as serialized TL, so they stay readable and survive schema regeneration.
/// </summary>
public class PassportErrorStore(IMongoDatabase mongoDatabase) : IPassportErrorStore, ISingletonDependency
{
    private const string CollectionName = "passport_errors";

    private IMongoCollection<PassportErrorDocument> Collection =>
        mongoDatabase.GetCollection<PassportErrorDocument>(CollectionName);

    public async Task SetAsync(long userId, long botId, IReadOnlyList<ISecureValueError> errors)
    {
        await ClearAsync(userId, botId);

        var documents = errors.Select(e => ToDocument(userId, botId, e)).OfType<PassportErrorDocument>()
            // A bot may legitimately send two errors that decompose to the same key (same kind, type and
            // hash) with different texts; the last one wins, as it would on the official server.
            .GroupBy(d => d.Id)
            .Select(g => g.Last())
            .ToList();

        if (documents.Count > 0)
        {
            await Collection.InsertManyAsync(documents);
        }
    }

    public async Task<TVector<ISecureValueError>> GetAsync(long userId, long botId)
    {
        var documents = await Collection.Find(d => d.UserId == userId && d.BotId == botId)
            .Sort(Builders<PassportErrorDocument>.Sort.Ascending(d => d.Date))
            .ToListAsync();

        var result = new TVector<ISecureValueError>();
        foreach (var error in documents.Select(ToTlObject).OfType<ISecureValueError>())
        {
            result.Add(error);
        }

        return result;
    }

    public async Task ClearAsync(long userId, long botId)
    {
        await Collection.DeleteManyAsync(d => d.UserId == userId && d.BotId == botId);
    }

    public async Task ClearAllAsync(long userId)
    {
        await Collection.DeleteManyAsync(d => d.UserId == userId);
    }

    private static PassportErrorDocument? ToDocument(long userId, long botId, ISecureValueError error)
    {
        var document = new PassportErrorDocument
        {
            UserId = userId,
            BotId = botId,
            Kind = error.ConstructorId,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        switch (error)
        {
            case TSecureValueError e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.Hash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorData e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.DataHash.ToArray();
                document.Field = e.Field;
                document.Text = e.Text;
                break;
            case TSecureValueErrorFrontSide e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.FileHash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorReverseSide e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.FileHash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorSelfie e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.FileHash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorFile e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.FileHash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorTranslationFile e:
                document.Type = e.Type.ConstructorId;
                document.Hash = e.FileHash.ToArray();
                document.Text = e.Text;
                break;
            case TSecureValueErrorFiles e:
                document.Type = e.Type.ConstructorId;
                document.Hashes = e.FileHash.Select(h => h.ToArray()).ToList();
                document.Text = e.Text;
                break;
            case TSecureValueErrorTranslationFiles e:
                document.Type = e.Type.ConstructorId;
                document.Hashes = e.FileHash.Select(h => h.ToArray()).ToList();
                document.Text = e.Text;
                break;
            default:
                return null;
        }

        document.Id = BuildId(document);

        return document;
    }

    private static ISecureValueError? ToTlObject(PassportErrorDocument document)
    {
        var type = PassportValueTypes.Create((uint)document.Type);
        if (type == null)
        {
            return null;
        }

        var hash = document.Hash ?? [];

        return (uint)document.Kind switch
        {
            0x869d758f => new TSecureValueError { Type = type, Hash = hash, Text = document.Text },
            0xe8a40bd9 => new TSecureValueErrorData
            {
                Type = type, DataHash = hash, Field = document.Field ?? string.Empty, Text = document.Text
            },
            0x00be3dfa => new TSecureValueErrorFrontSide { Type = type, FileHash = hash, Text = document.Text },
            0x868a2aa5 => new TSecureValueErrorReverseSide { Type = type, FileHash = hash, Text = document.Text },
            0xe537ced6 => new TSecureValueErrorSelfie { Type = type, FileHash = hash, Text = document.Text },
            0x7a700873 => new TSecureValueErrorFile { Type = type, FileHash = hash, Text = document.Text },
            0xa1144770 => new TSecureValueErrorTranslationFile { Type = type, FileHash = hash, Text = document.Text },
            0x666220e9 => new TSecureValueErrorFiles
            {
                Type = type, FileHash = ToHashVector(document.Hashes), Text = document.Text
            },
            0x34636dd8 => new TSecureValueErrorTranslationFiles
            {
                Type = type, FileHash = ToHashVector(document.Hashes), Text = document.Text
            },
            _ => null
        };
    }

    private static TVector<ReadOnlyMemory<byte>> ToHashVector(List<byte[]> hashes)
    {
        var vector = new TVector<ReadOnlyMemory<byte>>();
        foreach (var hash in hashes)
        {
            vector.Add(hash);
        }

        return vector;
    }

    private static string BuildId(PassportErrorDocument document)
    {
        var hashKey = document.Hash is { Length: > 0 }
            ? Convert.ToHexString(document.Hash)
            : string.Join('_', document.Hashes.Select(Convert.ToHexString));

        return $"{document.UserId}:{document.BotId}:{document.Kind}:{document.Type}:{hashKey}";
    }
}
