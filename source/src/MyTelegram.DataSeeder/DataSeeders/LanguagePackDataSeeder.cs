using System.Text.Json.Serialization;
using EventFlow.MongoDB.ReadStores;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.DataSeeder.DataSeeders;

public sealed class LanguagePackDataSeeder(
    IMongoDatabase database,
    IReadModelDescriptionProvider readModelDescriptionProvider,
    IDataSeederHelper dataSeederHelper,
    ILogger<LanguagePackDataSeeder> logger) : IDataSeeder, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task SeedAsync()
    {
        var rootFolder = Path.Combine(
            AppContext.BaseDirectory,
            DataSeederConsts.RootFolder,
            DataSeederConsts.LanguagePacksRootFolder);

        if (!Directory.Exists(rootFolder))
        {
            logger.LogWarning("Language pack folder is missing: {Folder}", rootFolder);
            return;
        }

        var files = Directory
            .EnumerateFiles(rootFolder, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            logger.LogWarning("Language pack folder contains no json files: {Folder}", rootFolder);
            return;
        }

        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        var languageCollection = database.GetCollection<BsonDocument>(GetCollectionName<LanguageReadModel>());
        var languageTextCollection = database.GetCollection<BsonDocument>(GetCollectionName<LanguageTextReadModel>());
        var importedCount = 0;
        var skippedCount = 0;
        var invalidCount = 0;
        var unsupportedCount = 0;
        var importedStringsCount = 0;
        var configChanged = false;

        foreach (var fileName in files)
        {
            LanguagePackSnapshot? languagePack;
            try
            {
                languagePack = await ReadLanguagePackAsync(fileName);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                invalidCount++;
                logger.LogWarning(ex, "Language pack cannot be parsed: {FileName}", fileName);
                continue;
            }

            if (languagePack?.Strings.Count is null or 0)
            {
                invalidCount++;
                logger.LogWarning("Language pack contains no strings or cannot be parsed: {FileName}", fileName);
                continue;
            }

            var languageCode = GetLanguageCode(languagePack, fileName);
            var languagePackName = GetLanguagePackName(languagePack, fileName);
            if (!TryGetPlatform(languagePackName, out var platform))
            {
                unsupportedCount++;
                logger.LogWarning(
                    "Language pack platform is not supported: {LanguagePack}, file: {FileName}",
                    languagePackName,
                    fileName);
                continue;
            }

            var languageVersion = languagePack.Version <= 0 ? 1 : languagePack.Version;
            var importKey = $"{languageCode}:{languagePackName}";
            if (config.ImportedLanguagePackVersions.TryGetValue(importKey, out var importedVersion) &&
                importedVersion == languageVersion)
            {
                skippedCount++;
                continue;
            }

            await UpsertLanguageAsync(languageCollection, platform, languageCode, languagePack, languageVersion);
            await UpsertLanguageTextsAsync(
                languageTextCollection,
                platform,
                languageCode,
                languageVersion,
                languagePack.Strings);

            config.ImportedLanguagePackVersions[importKey] = languageVersion;
            configChanged = true;
            importedCount++;
            importedStringsCount += languagePack.Strings.Count;
        }

        if (configChanged)
        {
            await dataSeederHelper.SaveDataSeederConfigAsync();
        }

        logger.LogInformation(
            "Language packs import completed. Imported: {ImportedCount}, skipped: {SkippedCount}, invalid: {InvalidCount}, unsupported: {UnsupportedCount}, strings: {StringsCount}",
            importedCount,
            skippedCount,
            invalidCount,
            unsupportedCount,
            importedStringsCount);
    }

    private string GetCollectionName<TReadModel>()
        where TReadModel : IMongoDbReadModel
    {
        return readModelDescriptionProvider.GetReadModelDescription<TReadModel>().RootCollectionName.Value;
    }

    private static async Task<LanguagePackSnapshot?> ReadLanguagePackAsync(string fileName)
    {
        await using var stream = File.OpenRead(fileName);
        return await JsonSerializer.DeserializeAsync<LanguagePackSnapshot>(stream, JsonSerializerOptions);
    }

    private static async Task UpsertLanguageAsync(
        IMongoCollection<BsonDocument> collection,
        DeviceType platform,
        string languageCode,
        LanguagePackSnapshot languagePack,
        int languageVersion)
    {
        var id = GetLanguageId(languageCode, platform);
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("_id", id)
            .Set("Platform", (int)platform)
            .Set("Rtl", languagePack.Rtl)
            .Set("Name", languagePack.Name)
            .Set("NativeName", languagePack.NativeName)
            .Set("LanguageCode", languageCode)
            .Set("PluralCode", languagePack.PluralCode)
            .Set("TranslationsUrl", languagePack.Source)
            .Set("IsEnabled", true)
            .Set("TranslatedCount", languagePack.Strings.Count)
            .Set("LanguageVersion", languageVersion)
            .Set("Version", BsonInt64.Create(languageVersion));

        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", id),
            update,
            new UpdateOptions { IsUpsert = true });
    }

    private static async Task UpsertLanguageTextsAsync(
        IMongoCollection<BsonDocument> collection,
        DeviceType platform,
        string languageCode,
        int languageVersion,
        IReadOnlyCollection<LanguagePackStringSnapshot> strings)
    {
        var writes = new List<WriteModel<BsonDocument>>(strings.Count);
        foreach (var item in strings)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                continue;
            }

            var id = GetLanguageTextId(languageCode, platform, item.Key);
            var update = Builders<BsonDocument>.Update
                .SetOnInsert("_id", id)
                .Set("Platform", (int)platform)
                .Set("LanguageCode", languageCode)
                .Set("Key", item.Key)
                .Set("Value", ToBsonValue(item.Value))
                .Set("ZeroValue", ToBsonValue(item.ZeroValue))
                .Set("OneValue", ToBsonValue(item.OneValue))
                .Set("TwoValue", ToBsonValue(item.TwoValue))
                .Set("FewValue", ToBsonValue(item.FewValue))
                .Set("ManyValue", ToBsonValue(item.ManyValue))
                .Set("OtherValue", ToBsonValue(item.OtherValue))
                .Set("LanguageVersion", languageVersion)
                .Set("Version", BsonInt64.Create(languageVersion));

            writes.Add(new UpdateOneModel<BsonDocument>(
                Builders<BsonDocument>.Filter.Eq("_id", id),
                update)
            {
                IsUpsert = true
            });
        }

        if (writes.Count > 0)
        {
            await collection.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false });
        }
    }

    private static string GetLanguageCode(LanguagePackSnapshot languagePack, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(languagePack.LanguageCode))
        {
            return languagePack.LanguageCode.Trim();
        }

        return new DirectoryInfo(Path.GetDirectoryName(fileName) ?? string.Empty).Name;
    }

    private static string GetLanguagePackName(LanguagePackSnapshot languagePack, string fileName)
    {
        var languagePackName = string.IsNullOrWhiteSpace(languagePack.LanguagePack)
            ? Path.GetFileNameWithoutExtension(fileName)
            : languagePack.LanguagePack;

        return NormalizeLanguagePack(languagePackName);
    }

    private static bool TryGetPlatform(string languagePack, out DeviceType platform)
    {
        switch (NormalizeLanguagePack(languagePack))
        {
            case "android":
                platform = DeviceType.Android;
                return true;
            case "android_x":
                platform = DeviceType.AndroidX;
                return true;
            case "tdesktop":
                platform = DeviceType.Desktop;
                return true;
            case "ios":
                platform = DeviceType.Ios;
                return true;
            case "macos":
                platform = DeviceType.MacOs;
                return true;
            case "tdlib":
                platform = DeviceType.TdLib;
                return true;
            case "unigram":
                platform = DeviceType.Unigram;
                return true;
            case "weba":
                platform = DeviceType.WebA;
                return true;
            case "webk":
                platform = DeviceType.WebK;
                return true;
            default:
                platform = DeviceType.Unknown;
                return false;
        }
    }

    private static string NormalizeLanguagePack(string languagePack)
    {
        var normalized = languagePack.Trim().ToLowerInvariant();
        return normalized switch
        {
            "desktop" or "telegramdesktop" => "tdesktop",
            "androidx" or "android-x" => "android_x",
            "macosx" or "mac-os" or "mac_os" => "macos",
            "web-a" or "web_a" => "weba",
            "web-k" or "web_k" => "webk",
            _ => normalized
        };
    }

    private static BsonValue ToBsonValue(string? value) =>
        string.IsNullOrEmpty(value) ? BsonNull.Value : value;

    private static string GetLanguageId(string languageCode, DeviceType platform)
    {
        return $"{languageCode}_{platform}".ToLowerInvariant();
    }

    private static string GetLanguageTextId(string languageCode, DeviceType platform, string key)
    {
        return $"{languageCode}_{platform}_{key}".ToLowerInvariant();
    }

    private sealed record LanguagePackSnapshot
    {
        public string Source { get; init; } = string.Empty;
        public string LanguageCode { get; init; } = string.Empty;
        public string LanguagePack { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string NativeName { get; init; } = string.Empty;
        public string PluralCode { get; init; } = string.Empty;
        public bool Rtl { get; init; }
        public int Version { get; init; }
        public Dictionary<string, int> Sections { get; init; } = [];
        public List<LanguagePackStringSnapshot> Strings { get; init; } = [];
    }

    private sealed record LanguagePackStringSnapshot
    {
        [JsonPropertyName("key")]
        public string Key { get; init; } = string.Empty;

        [JsonPropertyName("section")]
        public string? Section { get; init; }

        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("zeroValue")]
        public string? ZeroValue { get; init; }

        [JsonPropertyName("oneValue")]
        public string? OneValue { get; init; }

        [JsonPropertyName("twoValue")]
        public string? TwoValue { get; init; }

        [JsonPropertyName("fewValue")]
        public string? FewValue { get; init; }

        [JsonPropertyName("manyValue")]
        public string? ManyValue { get; init; }

        [JsonPropertyName("otherValue")]
        public string? OtherValue { get; init; }
    }
}
