using System.Security.Cryptography;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Passport;

/// <summary>
/// Mongo-backed <see cref="IPassportValueStore"/>. See the interface for the contract.
/// </summary>
public class PassportValueStore(
    IMongoDatabase mongoDatabase,
    IPassportFileStore passportFileStore) : IPassportValueStore, ISingletonDependency
{
    private const string CollectionName = "passport_values";

    private IMongoCollection<PassportValueDocument> Collection =>
        mongoDatabase.GetCollection<PassportValueDocument>(CollectionName);

    public async Task<PassportValueDocument> SaveAsync(long userId, IInputSecureValue value)
    {
        var input = (TInputSecureValue)value;
        var type = input.Type.ConstructorId;

        var document = new PassportValueDocument
        {
            Id = BuildId(userId, type),
            UserId = userId,
            Type = type,
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (input.Data is TSecureData data)
        {
            document.Data = data.Data.ToArray();
            document.DataHash = data.DataHash.ToArray();
            document.DataSecret = data.Secret.ToArray();
        }

        switch (input.PlainData)
        {
            case TSecurePlainPhone phone:
                document.PlainPhone = phone.Phone;
                break;
            case TSecurePlainEmail email:
                document.PlainEmail = email.Email;
                break;
        }

        // Resolve the files first: a value that references a file the user does not own must fail
        // before the previous value of this type is overwritten.
        var resolved = new Dictionary<long, PassportFileDocument>();
        var stored = new List<long>();

        try
        {
            document.FrontSideFileId = await StoreFileAsync(userId, input.FrontSide, resolved, stored);
            document.ReverseSideFileId = await StoreFileAsync(userId, input.ReverseSide, resolved, stored);
            document.SelfieFileId = await StoreFileAsync(userId, input.Selfie, resolved, stored);
            document.FileIds = await StoreFilesAsync(userId, input.Files, resolved, stored);
            document.TranslationFileIds = await StoreFilesAsync(userId, input.Translation, resolved, stored);
        }
        catch
        {
            // A value is stored whole or not at all. Files already moved out of the upload staging area
            // are unreachable once the call fails, so a client retrying a partly-invalid value would
            // otherwise leave a copy of every scan behind on each attempt.
            await passportFileStore.DeleteAsync(stored, userId);
            throw;
        }

        document.Hash = ComputeValueHash(document, resolved);

        var previous = await Collection.Find(d => d.Id == document.Id).FirstOrDefaultAsync();

        await Collection.ReplaceOneAsync(d => d.Id == document.Id, document, new ReplaceOptions { IsUpsert = true });

        if (previous != null)
        {
            // Files the replaced value referenced and the new one does not are orphaned; drop them so a
            // re-uploaded document does not leave its predecessor's scans in the database forever.
            var keep = document.AllFileIds().ToHashSet();
            var orphans = previous.AllFileIds().Where(id => !keep.Contains(id)).ToList();
            await passportFileStore.DeleteAsync(orphans, userId);
        }

        return document;
    }

    public async Task<List<PassportValueDocument>> GetAllAsync(long userId)
    {
        return await Collection.Find(d => d.UserId == userId)
            .Sort(Builders<PassportValueDocument>.Sort.Ascending(d => d.Type))
            .ToListAsync();
    }

    public async Task<List<PassportValueDocument>> GetAsync(long userId, IReadOnlyCollection<uint> types)
    {
        if (types.Count == 0)
        {
            return [];
        }

        var ids = types.Select(t => BuildId(userId, t)).ToList();
        var documents = await Collection.Find(Builders<PassportValueDocument>.Filter.In(d => d.Id, ids))
            .ToListAsync();

        var byType = documents.ToDictionary(d => (uint)d.Type);

        return types.Where(byType.ContainsKey).Select(t => byType[t]).ToList();
    }

    public async Task DeleteAsync(long userId, IReadOnlyCollection<uint> types)
    {
        var documents = await GetAsync(userId, types);
        if (documents.Count == 0)
        {
            return;
        }

        await passportFileStore.DeleteAsync(documents.SelectMany(d => d.AllFileIds()).ToList(), userId);
        await Collection.DeleteManyAsync(
            Builders<PassportValueDocument>.Filter.In(d => d.Id, documents.Select(d => d.Id)));
    }

    public async Task DeleteAllAsync(long userId)
    {
        await Collection.DeleteManyAsync(d => d.UserId == userId);
        await passportFileStore.DeleteAllAsync(userId);
    }

    public async Task<bool> HasAnyAsync(long userId)
    {
        return await Collection.Find(d => d.UserId == userId).AnyAsync();
    }

    public async Task<TVector<ISecureValue>> ToSecureValuesAsync(long userId,
        IReadOnlyList<PassportValueDocument> documents)
    {
        var result = new TVector<ISecureValue>();
        if (documents.Count == 0)
        {
            return result;
        }

        var fileIds = documents.SelectMany(d => d.AllFileIds()).Distinct().ToList();
        var files = await passportFileStore.GetManyAsync(fileIds, userId);

        foreach (var document in documents)
        {
            var type = PassportValueTypes.Create((uint)document.Type);
            if (type == null)
            {
                continue;
            }

            var value = new TSecureValue
            {
                Type = type,
                Hash = document.Hash,
                // Vectors are only serialised when their flag bit is set, but leaving them null would
                // still trip anything that walks the object graph (access-hash rewriting included).
                Translation = new TVector<ISecureFile>(),
                Files = new TVector<ISecureFile>()
            };

            if (document.Data is { Length: > 0 })
            {
                value.Data = new TSecureData
                {
                    Data = document.Data,
                    DataHash = document.DataHash ?? [],
                    Secret = document.DataSecret ?? []
                };
            }

            value.FrontSide = ToSecureFile(document.FrontSideFileId, files);
            value.ReverseSide = ToSecureFile(document.ReverseSideFileId, files);
            value.Selfie = ToSecureFile(document.SelfieFileId, files);

            foreach (var file in document.FileIds.Select(id => ToSecureFile(id, files)).OfType<ISecureFile>())
            {
                value.Files.Add(file);
            }

            foreach (var file in document.TranslationFileIds.Select(id => ToSecureFile(id, files))
                         .OfType<ISecureFile>())
            {
                value.Translation.Add(file);
            }

            if (document.PlainPhone != null)
            {
                value.PlainData = new TSecurePlainPhone { Phone = document.PlainPhone };
            }
            else if (document.PlainEmail != null)
            {
                value.PlainData = new TSecurePlainEmail { Email = document.PlainEmail };
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// <c>secureValue.hash</c>. Clients take this value from the server verbatim (tdlib
    /// <c>get_encrypted_secure_value</c>) and quote it back in <c>value_hashes</c>, so the only
    /// requirement is that it be stable and derived from the whole value. Plain values hash the
    /// plaintext itself, which is what tdlib's own <c>calc_value_hash</c> does for them; encrypted
    /// values hash the (data_hash, secret) pairs in the field order tdlib uses. The plaintext file
    /// secrets tdlib mixes in are unavailable here by design — the server never has a key.
    /// </summary>
    private static byte[] ComputeValueHash(PassportValueDocument document,
        IReadOnlyDictionary<long, PassportFileDocument> files)
    {
        if (document.PlainPhone != null)
        {
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document.PlainPhone));
        }

        if (document.PlainEmail != null)
        {
            return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document.PlainEmail));
        }

        var buffer = new List<byte>();

        if (document.DataHash is { Length: > 0 })
        {
            buffer.AddRange(document.DataHash);
            buffer.AddRange(document.DataSecret ?? []);
        }

        foreach (var fileId in document.AllFileIds())
        {
            if (!files.TryGetValue(fileId, out var file))
            {
                continue;
            }

            buffer.AddRange(file.FileHash);
            buffer.AddRange(file.Secret);
        }

        return SHA256.HashData(buffer.ToArray());
    }

    private static ISecureFile? ToSecureFile(long? fileId, IReadOnlyDictionary<long, PassportFileDocument> files)
    {
        if (!fileId.HasValue || !files.TryGetValue(fileId.Value, out var file))
        {
            return null;
        }

        return new TSecureFile
        {
            Id = file.Id,
            AccessHash = file.AccessHash,
            Size = file.Size,
            DcId = file.DcId,
            Date = file.Date,
            FileHash = file.FileHash,
            Secret = file.Secret
        };
    }

    private async Task<long?> StoreFileAsync(long userId,
        IInputSecureFile? file,
        Dictionary<long, PassportFileDocument> resolved,
        List<long> stored)
    {
        switch (file)
        {
            case TInputSecureFileUploaded uploaded:
            {
                var document = await passportFileStore.StoreUploadedAsync(userId,
                    uploaded.Id,
                    uploaded.Parts,
                    uploaded.Md5Checksum,
                    uploaded.FileHash.ToArray(),
                    uploaded.Secret.ToArray());
                resolved[document.Id] = document;
                stored.Add(document.Id);
                return document.Id;
            }
            case TInputSecureFile existing:
            {
                var document = await passportFileStore.GetAsync(existing.Id, userId);
                if (document == null)
                {
                    RpcErrors.RpcErrors400.FileIdInvalid.ThrowRpcError();
                }

                resolved[document!.Id] = document;
                return document.Id;
            }
            default:
                return null;
        }
    }

    private async Task<List<long>> StoreFilesAsync(long userId,
        TVector<IInputSecureFile>? files,
        Dictionary<long, PassportFileDocument> resolved,
        List<long> stored)
    {
        var result = new List<long>();
        if (files == null)
        {
            return result;
        }

        foreach (var file in files)
        {
            var id = await StoreFileAsync(userId, file, resolved, stored);
            if (id.HasValue)
            {
                result.Add(id.Value);
            }
        }

        return result;
    }

    private static string BuildId(long userId, uint type) => $"{userId}:{type}";
}
