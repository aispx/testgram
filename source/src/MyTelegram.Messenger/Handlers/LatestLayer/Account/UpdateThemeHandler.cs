using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Update theme
/// Possible errors
/// Code Type Description
/// 400 THEME_INVALID Invalid theme provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.updateTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 222: account.updateTheme#2bf40ccc flags:# format:string theme:InputTheme slug:flags.0?string title:flags.1?string document:flags.2?InputDocument settings:flags.3?Vector<InputThemeSettings> = Theme;
/// </remarks>
internal sealed class UpdateThemeHandler(
    IMongoDatabase database,
    ILogger<UpdateThemeHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestUpdateTheme, MyTelegram.Schema.ITheme>
{
    protected override async Task<MyTelegram.Schema.ITheme> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestUpdateTheme obj)
    {
        // Extract theme ID
        long themeId = 0;
        if (obj.Theme is MyTelegram.Schema.TInputTheme inputTheme)
        {
            themeId = inputTheme.Id;
        }
        else if (obj.Theme is MyTelegram.Schema.TInputThemeSlug inputThemeSlug)
        {
            // Find theme by slug
            var themesCol = database.GetCollection<BsonDocument>("themes");
            var themeDoc = await themesCol.Find(
                Builders<BsonDocument>.Filter.Eq("Slug", inputThemeSlug.Slug)
            ).FirstOrDefaultAsync();

            if (themeDoc != null)
            {
                themeId = themeDoc["ThemeId"].AsInt64;
            }
        }

        if (themeId == 0)
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
        }

        // Get theme from database
        var collection = database.GetCollection<BsonDocument>("themes");
        var filter = Builders<BsonDocument>.Filter.Eq("ThemeId", themeId);
        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        if (doc == null)
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
        }

        // Verify user is the creator
        if (doc["CreatorUserId"].AsInt64 != input.UserId)
        {
            RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
        }

        // Build update
        var update = Builders<BsonDocument>.Update;
        var updates = new List<UpdateDefinition<BsonDocument>>();

        // Update slug if provided
        if (!string.IsNullOrWhiteSpace(obj.Slug))
        {
            // Check if new slug is available
            var existingTheme = await collection.Find(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug),
                    Builders<BsonDocument>.Filter.Ne("ThemeId", themeId)
                )
            ).FirstOrDefaultAsync();

            if (existingTheme != null)
            {
                RpcErrors.RpcErrors400.ThemeInvalid.ThrowRpcError();
            }

            updates.Add(update.Set("Slug", obj.Slug));
        }

        // Update title if provided
        if (!string.IsNullOrWhiteSpace(obj.Title))
        {
            if (obj.Title.Length > 128)
            {
                RpcErrors.RpcErrors400.ThemeTitleInvalid.ThrowRpcError();
            }
            updates.Add(update.Set("Title", obj.Title));
        }

        // Update document if provided
        if (obj.Document is MyTelegram.Schema.TInputDocument inputDoc)
        {
            updates.Add(update.Set("DocumentId", inputDoc.Id));
        }

        // Update settings if provided
        if (obj.Settings != null && obj.Settings.Count > 0)
        {
            var settingsList = new BsonArray();
            foreach (var setting in obj.Settings)
            {
                if (setting is MyTelegram.Schema.TInputThemeSettings inputSettings)
                {
                    var settingDoc = new BsonDocument
                    {
                        ["BaseTheme"] = GetBaseThemeName(inputSettings.BaseTheme),
                        ["AccentColor"] = inputSettings.AccentColor,
                        ["MessageColorsAnimated"] = inputSettings.MessageColorsAnimated
                    };

                    if (inputSettings.OutboxAccentColor.HasValue)
                    {
                        settingDoc["OutboxAccentColor"] = inputSettings.OutboxAccentColor.Value;
                    }

                    if (inputSettings.MessageColors != null && inputSettings.MessageColors.Count > 0)
                    {
                        settingDoc["MessageColors"] = new BsonArray(inputSettings.MessageColors.Select(c => (BsonValue)c));
                    }

                    settingsList.Add(settingDoc);
                }
            }
            updates.Add(update.Set("Settings", settingsList));
        }

        // Update modified timestamp
        updates.Add(update.Set("UpdatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        if (updates.Count > 0)
        {
            await collection.UpdateOneAsync(filter, update.Combine(updates));
        }

        // Reload theme
        doc = await collection.Find(filter).FirstOrDefaultAsync();

        logger.LogInformation("Theme updated: ThemeId={ThemeId}, UserId={UserId}", themeId, input.UserId);

        // Build response
        var theme = new MyTelegram.Schema.TTheme
        {
            Creator = true,
            Id = doc["ThemeId"].AsInt64,
            AccessHash = doc["AccessHash"].AsInt64,
            Slug = doc["Slug"].AsString,
            Title = doc["Title"].AsString,
            InstallsCount = doc.Contains("InstallsCount") ? doc["InstallsCount"].AsInt32 : 0
        };

        // Add settings if available
        if (doc.Contains("Settings") && doc["Settings"].IsBsonArray)
        {
            theme.Settings = ConvertToThemeSettings(doc["Settings"].AsBsonArray);
        }

        return theme;
    }

    private static string GetBaseThemeName(MyTelegram.Schema.IBaseTheme baseTheme)
    {
        return baseTheme switch
        {
            MyTelegram.Schema.TBaseThemeClassic => "classic",
            MyTelegram.Schema.TBaseThemeDay => "day",
            MyTelegram.Schema.TBaseThemeNight => "night",
            MyTelegram.Schema.TBaseThemeTinted => "tinted",
            MyTelegram.Schema.TBaseThemeArctic => "arctic",
            _ => "classic"
        };
    }

    private static TVector<MyTelegram.Schema.IThemeSettings> ConvertToThemeSettings(BsonArray settingsArray)
    {
        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

        foreach (var item in settingsArray)
        {
            if (item.IsBsonDocument)
            {
                var settingDoc = item.AsBsonDocument;
                var baseThemeName = settingDoc["BaseTheme"].AsString;

                var setting = new MyTelegram.Schema.TThemeSettings
                {
                    BaseTheme = GetBaseTheme(baseThemeName),
                    AccentColor = settingDoc["AccentColor"].AsInt32,
                    MessageColorsAnimated = settingDoc.Contains("MessageColorsAnimated") && settingDoc["MessageColorsAnimated"].AsBoolean
                };

                if (settingDoc.Contains("OutboxAccentColor"))
                {
                    setting.OutboxAccentColor = settingDoc["OutboxAccentColor"].AsInt32;
                }

                if (settingDoc.Contains("MessageColors") && settingDoc["MessageColors"].IsBsonArray)
                {
                    setting.MessageColors = new TVector<int>(
                        settingDoc["MessageColors"].AsBsonArray.Select(c => c.AsInt32)
                    );
                }

                settings.Add(setting);
            }
        }

        return settings;
    }

    private static MyTelegram.Schema.IBaseTheme GetBaseTheme(string name)
    {
        return name switch
        {
            "classic" => new MyTelegram.Schema.TBaseThemeClassic(),
            "day" => new MyTelegram.Schema.TBaseThemeDay(),
            "night" => new MyTelegram.Schema.TBaseThemeNight(),
            "tinted" => new MyTelegram.Schema.TBaseThemeTinted(),
            "arctic" => new MyTelegram.Schema.TBaseThemeArctic(),
            _ => new MyTelegram.Schema.TBaseThemeClassic()
        };
    }
}