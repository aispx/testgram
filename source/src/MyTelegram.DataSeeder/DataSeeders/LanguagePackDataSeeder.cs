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
    private const string RussianLanguageCode = "ru";
    private static readonly DeviceType[] RussianAndroidTargetPlatforms =
    [
        DeviceType.Android,
        DeviceType.AndroidX,
        DeviceType.Desktop,
        DeviceType.Ios,
        DeviceType.MacOs,
        DeviceType.TdLib,
        DeviceType.Unigram,
        DeviceType.WebA,
        DeviceType.WebK
    ];

    public async Task SeedAsync()
    {
        var jsonText = await dataSeederHelper.GetJsonTextAsync(DataSeederConsts.RussianAndroidLangPackFileName);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            logger.LogWarning("Russian language pack file is missing: {FileName}",
                DataSeederConsts.RussianAndroidLangPackFileName);
            return;
        }

        var languagePack = JsonSerializer.Deserialize<LanguagePackSnapshot>(jsonText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (languagePack?.Strings.Count is null or 0)
        {
            logger.LogWarning("Russian language pack contains no strings: {FileName}",
                DataSeederConsts.RussianAndroidLangPackFileName);
            return;
        }

        var languageVersion = languagePack.Version <= 0 ? 1 : languagePack.Version;
        var importKey = $"{RussianLanguageCode}:official-android";
        var config = await dataSeederHelper.LoadDataSeederConfigAsync();
        if (config.ImportedLanguagePackVersions.TryGetValue(importKey, out var importedVersion) &&
            importedVersion == languageVersion)
        {
            logger.LogInformation("Russian language pack is already imported, version: {Version}", languageVersion);
            return;
        }

        var languageCollection = database.GetCollection<BsonDocument>(GetCollectionName<LanguageReadModel>());
        var languageTextCollection = database.GetCollection<BsonDocument>(GetCollectionName<LanguageTextReadModel>());

        foreach (var platform in RussianAndroidTargetPlatforms)
        {
            await UpsertLanguageAsync(languageCollection, platform, languagePack, languageVersion);
            await UpsertLanguageTextsAsync(languageTextCollection, platform, languageVersion, languagePack.Strings);
        }

        config.ImportedLanguagePackVersions[importKey] = languageVersion;
        await dataSeederHelper.SaveDataSeederConfigAsync();
        logger.LogInformation(
            "Russian language pack imported from official Telegram Android resources, version: {Version}, strings: {StringsCount}, platforms: {PlatformsCount}",
            languageVersion,
            languagePack.Strings.Count,
            RussianAndroidTargetPlatforms.Length);
    }

    private string GetCollectionName<TReadModel>()
        where TReadModel : IMongoDbReadModel
    {
        return readModelDescriptionProvider.GetReadModelDescription<TReadModel>().RootCollectionName.Value;
    }

    private static async Task UpsertLanguageAsync(
        IMongoCollection<BsonDocument> collection,
        DeviceType platform,
        LanguagePackSnapshot languagePack,
        int languageVersion)
    {
        var id = GetLanguageId(RussianLanguageCode, platform);
        var update = Builders<BsonDocument>.Update
            .SetOnInsert("_id", id)
            .Set("Platform", (int)platform)
            .Set("Rtl", false)
            .Set("Name", languagePack.Name)
            .Set("NativeName", languagePack.NativeName)
            .Set("LanguageCode", RussianLanguageCode)
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
        int languageVersion,
        IReadOnlyCollection<LanguagePackStringSnapshot> strings)
    {
        var writes = new List<WriteModel<BsonDocument>>(strings.Count);
        foreach (var item in strings)
        {
            var id = GetLanguageTextId(RussianLanguageCode, platform, item.Key);
            var update = Builders<BsonDocument>.Update
                .SetOnInsert("_id", id)
                .Set("Platform", (int)platform)
                .Set("LanguageCode", RussianLanguageCode)
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

    private sealed record LanguagePackSnapshot(
        string Source,
        string LanguageCode,
        string LanguagePack,
        string Name,
        string NativeName,
        string PluralCode,
        int Version,
        Dictionary<string, int> Sections,
        List<LanguagePackStringSnapshot> Strings);

    private sealed record LanguagePackStringSnapshot
    {
        [JsonPropertyName("key")]
        public required string Key { get; init; }

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
