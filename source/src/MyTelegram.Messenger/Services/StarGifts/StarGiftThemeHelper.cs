using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.StarGifts;

/// <summary>
/// Loads chat-theme settings for a unique star gift.
///
/// The theme is owned by the <b>gift type</b> (the star-gifts document
/// addressed by GiftId), not by each individual NFT.  This way every
/// collectible of the same gift — existing, freshly upgraded or recently
/// transferred — inherits the released theme automatically.
///
/// Source priority:
///   1. the unique-gift document itself (legacy per-NFT theme),
///   2. the gift-type document (released theme),
///   3. fallback generated from the backdrop attribute.
/// </summary>
public static class StarGiftThemeHelper
{
    public static TVector<MyTelegram.Schema.IThemeSettings>? LoadThemeSettings(
        BsonDocument uniqueGiftDoc,
        BsonDocument? giftTypeDoc = null)
    {
        var source = ReadThemeArray(uniqueGiftDoc)
                     ?? ReadThemeArray(giftTypeDoc);
        if (source == null)
        {
            return null;
        }

        var settings = new TVector<MyTelegram.Schema.IThemeSettings>();
        foreach (var settingDoc in source)
        {
            var setting = settingDoc.AsBsonDocument;
            var themeSettings = new MyTelegram.Schema.TThemeSettings
            {
                BaseTheme = setting["BaseTheme"].AsString == "classic"
                    ? new MyTelegram.Schema.TBaseThemeClassic()
                    : new MyTelegram.Schema.TBaseThemeNight(),
                AccentColor = setting["AccentColor"].ToInt32(),
                MessageColorsAnimated = setting.Contains("MessageColorsAnimated") && setting["MessageColorsAnimated"].AsBoolean,
                MessageColors = new TVector<int>(
                    setting["MessageColors"].AsBsonArray.Select(c => c.ToInt32()))
            };

            if (setting.Contains("OutboxAccentColor"))
            {
                themeSettings.OutboxAccentColor = setting["OutboxAccentColor"].ToInt32();
            }

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

        return settings;
    }

    private static BsonArray? ReadThemeArray(BsonDocument? doc)
    {
        if (doc == null) return null;
        return doc.Contains("ThemeSettings") && doc["ThemeSettings"].IsBsonArray
            ? doc["ThemeSettings"].AsBsonArray
            : null;
    }
}
