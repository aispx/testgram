using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Get all available chat <a href="https://corefork.telegram.org/api/themes">themes »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getChatThemes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// Themes are loaded from MongoDB collection "chat_themes"
/// </remarks>
internal sealed class GetChatThemesHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetChatThemes, MyTelegram.Schema.Account.IThemes>
{
    protected override async Task<MyTelegram.Schema.Account.IThemes> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetChatThemes obj)
    {
        var themes = new TVector<MyTelegram.Schema.ITheme>();

        // Load themes from MongoDB
        var collection = database.GetCollection<BsonDocument>("chat_themes");
        var themeDocs = await collection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();

        foreach (var doc in themeDocs)
        {
            var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

            if (doc.Contains("Settings") && doc["Settings"].IsBsonArray)
            {
                foreach (var settingDoc in doc["Settings"].AsBsonArray)
                {
                    var setting = settingDoc.AsBsonDocument;
                    var baseTheme = setting["BaseTheme"].AsString;

                    var themeSettings = new MyTelegram.Schema.TThemeSettings
                    {
                        BaseTheme = baseTheme == "classic"
                            ? new MyTelegram.Schema.TBaseThemeClassic()
                            : new MyTelegram.Schema.TBaseThemeNight(),
                        AccentColor = setting["AccentColor"].ToInt32(),
                        MessageColorsAnimated = setting.Contains("MessageColorsAnimated") && setting["MessageColorsAnimated"].AsBoolean,
                        MessageColors = new TVector<int>(
                            setting["MessageColors"].AsBsonArray.Select(c => c.ToInt32())
                        )
                    };

                    // OutboxAccentColor (optional, only for light theme)
                    if (setting.Contains("OutboxAccentColor"))
                    {
                        themeSettings.OutboxAccentColor = setting["OutboxAccentColor"].ToInt32();
                    }

                    // Wallpaper
                    if (setting.Contains("Wallpaper"))
                    {
                        var wp = setting["Wallpaper"].AsBsonDocument;
                        var wpSettings = wp["Settings"].AsBsonDocument;

                        themeSettings.Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
                        {
                            Id = wp["Id"].ToInt64(),
                            Dark = wp.Contains("Dark") && wp["Dark"].AsBoolean,
                            Settings = new MyTelegram.Schema.TWallPaperSettings
                            {
                                BackgroundColor = wpSettings["BackgroundColor"].ToInt32(),
                                Intensity = wpSettings["Intensity"].ToInt32()
                            }
                        };
                    }

                    settings.Add(themeSettings);
                }
            }

            themes.Add(new MyTelegram.Schema.TTheme
            {
                ForChat = doc["ForChat"].AsBoolean,
                Creator = doc["Creator"].AsBoolean,
                Default = doc["Default"].AsBoolean,
                Id = doc["ThemeId"].ToInt64(),
                AccessHash = doc["AccessHash"].ToInt64(),
                Slug = doc["Slug"].AsString,
                Title = doc["Title"].AsString,
                Emoticon = doc["Emoticon"].AsString,
                Settings = settings
            });
        }

        // Fallback to hardcoded themes if database is empty
        if (themes.Count == 0)
        {
            themes = GetFallbackThemes();
        }

        return new MyTelegram.Schema.Account.TThemes
        {
            Hash = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Themes = themes
        };
    }

    /// <summary>
    /// Fallback themes if database is empty
    /// </summary>
    private static TVector<MyTelegram.Schema.ITheme> GetFallbackThemes()
    {
        var themes = new TVector<MyTelegram.Schema.ITheme>();

        themes.Add(CreateChatTheme("🏠", 0x3390ec, 0x4fa8f5, new[] { 0x3390ec, 0x6fb1f6, 0x8ac5f8, 0xa5d8fa }, 0x2b7ec1, new[] { 0x2b7ec1, 0x3d8fd1, 0x4fa0e1, 0x61b1f1 }));
        themes.Add(CreateChatTheme("❤️", 0xff5da2, 0xff7ab8, new[] { 0xff5da2, 0xff7ab8, 0xff97ce, 0xffb4e4 }, 0xd14a85, new[] { 0xd14a85, 0xe15c97, 0xf16ea9, 0xff80bb }));
        themes.Add(CreateChatTheme("🌙", 0x8e7ec5, 0xa594d6, new[] { 0x8e7ec5, 0xa594d6, 0xbcaae7, 0xd3c0f8 }, 0x7565a3, new[] { 0x7565a3, 0x8676b4, 0x9787c5, 0xa898d6 }));
        themes.Add(CreateChatTheme("🔥", 0xff7f50, 0xff9970, new[] { 0xff7f50, 0xff9970, 0xffb390, 0xffcdb0 }, 0xd66a42, new[] { 0xd66a42, 0xe67b53, 0xf68c64, 0xff9d75 }));
        themes.Add(CreateChatTheme("⭐", 0xffc837, 0xffd557, new[] { 0xffc837, 0xffd557, 0xffe277, 0xffef97 }, 0xd6a72e, new[] { 0xd6a72e, 0xe6b73e, 0xf6c74e, 0xffd75e }));
        themes.Add(CreateChatTheme("🌸", 0xff9eb5, 0xffb4c8, new[] { 0xff9eb5, 0xffb4c8, 0xffcadb, 0xffe0ee }, 0xd68599, new[] { 0xd68599, 0xe695a9, 0xf6a5b9, 0xffb5c9 }));
        themes.Add(CreateChatTheme("🍀", 0x34c759, 0x54d779, new[] { 0x34c759, 0x54d779, 0x74e799, 0x94f7b9 }, 0x2ba84b, new[] { 0x2ba84b, 0x3bb85b, 0x4bc86b, 0x5bd87b }));
        themes.Add(CreateChatTheme("🎨", 0x9b7ff7, 0xb199f9, new[] { 0x9b7ff7, 0xb199f9, 0xc7b3fb, 0xddcdfd }, 0x826bd1, new[] { 0x826bd1, 0x927be1, 0xa28bf1, 0xb29bff }));
        themes.Add(CreateChatTheme("🐥", 0xffd700, 0xffe433, new[] { 0xffd700, 0xffe433, 0xfff166, 0xfffe99 }, 0xd6b500, new[] { 0xd6b500, 0xe6c510, 0xf6d520, 0xffe530 }));
        themes.Add(CreateChatTheme("🎮", 0x5b9cf5, 0x7bb2f7, new[] { 0x5b9cf5, 0x7bb2f7, 0x9bc8f9, 0xbbdefb }, 0x4c83d1, new[] { 0x4c83d1, 0x5c93e1, 0x6ca3f1, 0x7cb3ff }));

        return themes;
    }

    private static MyTelegram.Schema.ITheme CreateChatTheme(string emoticon, int lightAccent, int lightOutbox, int[] lightColors, int darkAccent, int[] darkColors)
    {
        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();

        settings.Add(new MyTelegram.Schema.TThemeSettings
        {
            BaseTheme = new MyTelegram.Schema.TBaseThemeClassic(),
            AccentColor = lightAccent,
            OutboxAccentColor = lightOutbox,
            MessageColorsAnimated = true,
            MessageColors = new TVector<int>(lightColors),
            Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = 0,
                Settings = new MyTelegram.Schema.TWallPaperSettings { BackgroundColor = unchecked((int)0xFFFFFFFF), Intensity = 0 }
            }
        });

        settings.Add(new MyTelegram.Schema.TThemeSettings
        {
            BaseTheme = new MyTelegram.Schema.TBaseThemeNight(),
            AccentColor = darkAccent,
            MessageColorsAnimated = true,
            MessageColors = new TVector<int>(darkColors),
            Wallpaper = new MyTelegram.Schema.TWallPaperNoFile
            {
                Id = 0,
                Dark = true,
                Settings = new MyTelegram.Schema.TWallPaperSettings { BackgroundColor = unchecked((int)0xFF0F0F0F), Intensity = 0 }
            }
        });

        return new MyTelegram.Schema.TTheme
        {
            ForChat = true,
            Creator = false,
            Default = false,
            Id = Math.Abs(emoticon.GetHashCode()),
            AccessHash = Random.Shared.NextInt64(),
            Slug = $"chat-theme-{emoticon}",
            Title = $"{emoticon} Chat Theme",
            Emoticon = emoticon,
            Settings = settings
        };
    }
}
