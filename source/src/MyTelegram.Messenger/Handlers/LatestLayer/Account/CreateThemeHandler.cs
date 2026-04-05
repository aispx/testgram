using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Create a theme
/// Possible errors
/// Code Type Description
/// 400 THEME_MIME_INVALID The theme's MIME type is invalid.
/// 400 THEME_TITLE_INVALID The specified theme title is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.createTheme"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Layer 222: account.createTheme#652e4400 flags:# slug:string title:string document:flags.2?InputDocument settings:flags.3?Vector<InputThemeSettings> = Theme;
/// </remarks>
internal sealed class CreateThemeHandler(
    IMongoDatabase database,
    ILogger<CreateThemeHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestCreateTheme, MyTelegram.Schema.ITheme>
{
    protected override async Task<MyTelegram.Schema.ITheme> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestCreateTheme obj)
    {
        // Validate title
        if (string.IsNullOrWhiteSpace(obj.Title) || obj.Title.Length > 128)
        {
            logger.LogWarning("Invalid theme title: {Title}", obj.Title);
            RpcErrors.RpcErrors400.ThemeTitleInvalid.ThrowRpcError();
        }

        // Generate theme ID and access hash
        var themeId = GenerateId();
        var accessHash = Random.Shared.NextInt64();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Generate slug if not provided
        var slug = string.IsNullOrWhiteSpace(obj.Slug)
            ? GenerateSlug(obj.Title)
            : obj.Slug;

        // Check if slug already exists
        var themesCol = database.GetCollection<BsonDocument>("themes");
        var existingTheme = await themesCol.Find(
            Builders<BsonDocument>.Filter.Eq("Slug", slug)
        ).FirstOrDefaultAsync();

        if (existingTheme != null)
        {
            // Generate unique slug
            slug = $"{slug}_{Random.Shared.Next(1000, 9999)}";
        }

        // Get document ID if provided
        long? documentId = null;
        if (obj.Document is MyTelegram.Schema.TInputDocument inputDoc)
        {
            documentId = inputDoc.Id;
        }

        // Build theme settings
        var settingsList = new BsonArray();
        if (obj.Settings != null)
        {
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
        }

        // Store theme in MongoDB
        var themeDoc = new BsonDocument
        {
            ["_id"] = $"theme-{themeId}",
            ["ThemeId"] = themeId,
            ["AccessHash"] = accessHash,
            ["Slug"] = slug,
            ["Title"] = obj.Title,
            ["CreatorUserId"] = input.UserId,
            ["CreatedAt"] = now,
            ["InstallsCount"] = 0
        };

        if (documentId.HasValue)
        {
            themeDoc["DocumentId"] = documentId.Value;
        }

        if (settingsList.Count > 0)
        {
            themeDoc["Settings"] = settingsList;
        }

        await themesCol.InsertOneAsync(themeDoc);

        // Auto-install for creator
        var userThemesCol = database.GetCollection<BsonDocument>("user_themes");
        await userThemesCol.InsertOneAsync(new BsonDocument
        {
            ["_id"] = $"user-theme-{input.UserId}-{themeId}",
            ["UserId"] = input.UserId,
            ["ThemeId"] = themeId,
            ["InstalledAt"] = now
        });

        logger.LogInformation("Theme created: ThemeId={ThemeId}, Slug={Slug}, UserId={UserId}",
            themeId, slug, input.UserId);

        // Build response
        var theme = new MyTelegram.Schema.TTheme
        {
            Creator = true,
            Id = themeId,
            AccessHash = accessHash,
            Slug = slug,
            Title = obj.Title,
            InstallsCount = 1
        };

        // Add settings if provided
        if (obj.Settings != null && obj.Settings.Count > 0)
        {
            theme.Settings = ConvertToThemeSettings(obj.Settings);
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

    private static TVector<MyTelegram.Schema.IThemeSettings> ConvertToThemeSettings(
        TVector<MyTelegram.Schema.IInputThemeSettings> inputSettings)
    {
        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

        foreach (var input in inputSettings)
        {
            if (input is MyTelegram.Schema.TInputThemeSettings inputSetting)
            {
                var setting = new MyTelegram.Schema.TThemeSettings
                {
                    BaseTheme = inputSetting.BaseTheme,
                    AccentColor = inputSetting.AccentColor,
                    MessageColorsAnimated = inputSetting.MessageColorsAnimated
                };

                if (inputSetting.OutboxAccentColor.HasValue)
                {
                    setting.OutboxAccentColor = inputSetting.OutboxAccentColor.Value;
                }

                if (inputSetting.MessageColors != null && inputSetting.MessageColors.Count > 0)
                {
                    setting.MessageColors = new TVector<int>(inputSetting.MessageColors);
                }

                settings.Add(setting);
            }
        }

        return settings;
    }

    private static string GenerateSlug(string title)
    {
        // Convert to lowercase and replace spaces with hyphens
        var slug = title.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        // Remove non-alphanumeric characters except hyphens
        slug = new string(slug.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

        // Limit length
        if (slug.Length > 64)
        {
            slug = slug.Substring(0, 64);
        }

        return slug;
    }

    private static long GenerateId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
    }
}