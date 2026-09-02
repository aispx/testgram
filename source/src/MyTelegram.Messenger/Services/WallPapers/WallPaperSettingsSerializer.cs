using MongoDB.Bson;

namespace MyTelegram.Messenger.Services.WallPapers;

/// <summary>
/// Moves a <c>wallPaperSettings</c> between its stored BSON shape and the TL constructor.
///
/// <para>There used to be four copies of this — one in each of
/// <c>GetWallPapersHandler</c>, <c>GetWallPaperHandler</c>, <c>GetMultiWallPapersHandler</c> and
/// <c>ChatWallPaperService</c> — and they had already drifted: only the first returned <c>null</c> for a
/// settings subdocument that named no value, so the other two answered with an empty
/// <c>wallPaperSettings</c> where the wallpaper has none. An empty settings object is not the same as an
/// absent one: it raises <c>flags.2</c> on the wallpaper and makes a client render a solid black fill
/// instead of falling back to its own default.</para>
/// </summary>
internal static class WallPaperSettingsSerializer
{
    /// <summary>
    /// Reads the stored subdocument. Returns null when it is absent or names nothing, so the
    /// <c>settings</c> flag stays clear.
    /// </summary>
    public static MyTelegram.Schema.IWallPaperSettings? FromBson(BsonValue value)
    {
        if (value.IsBsonNull || !value.IsBsonDocument)
        {
            return null;
        }

        var doc = value.AsBsonDocument;
        var settings = new MyTelegram.Schema.TWallPaperSettings
        {
            Blur = doc.GetValue("Blur", false).ToBoolean(),
            Motion = doc.GetValue("Motion", false).ToBoolean(),
            BackgroundColor = GetInt32(doc, "BackgroundColor"),
            SecondBackgroundColor = GetInt32(doc, "SecondBackgroundColor"),
            ThirdBackgroundColor = GetInt32(doc, "ThirdBackgroundColor"),
            FourthBackgroundColor = GetInt32(doc, "FourthBackgroundColor"),
            Intensity = GetInt32(doc, "Intensity"),
            Rotation = GetInt32(doc, "Rotation"),
            Emoticon = GetString(doc, "Emoticon")
        };

        return NamesNothing(settings) ? null : WallPaperSettingsHelper.PairSharedFlags(settings);
    }

    /// <summary>
    /// Writes the settings back. Returns <c>BsonNull</c> for settings that name nothing, so a
    /// round trip cannot turn an absent settings object into a present empty one.
    /// </summary>
    public static BsonValue ToBson(MyTelegram.Schema.IWallPaperSettings? settings)
    {
        if (settings is not MyTelegram.Schema.TWallPaperSettings wallPaperSettings ||
            NamesNothing(wallPaperSettings))
        {
            return BsonNull.Value;
        }

        var doc = new BsonDocument
        {
            { "Blur", wallPaperSettings.Blur },
            { "Motion", wallPaperSettings.Motion }
        };

        AddIfSet(doc, "BackgroundColor", wallPaperSettings.BackgroundColor);
        AddIfSet(doc, "SecondBackgroundColor", wallPaperSettings.SecondBackgroundColor);
        AddIfSet(doc, "ThirdBackgroundColor", wallPaperSettings.ThirdBackgroundColor);
        AddIfSet(doc, "FourthBackgroundColor", wallPaperSettings.FourthBackgroundColor);
        AddIfSet(doc, "Intensity", wallPaperSettings.Intensity);
        AddIfSet(doc, "Rotation", wallPaperSettings.Rotation);

        if (!string.IsNullOrEmpty(wallPaperSettings.Emoticon))
        {
            doc.Add("Emoticon", wallPaperSettings.Emoticon);
        }

        return doc;
    }

    /// <summary>The emoticon that marks a channel/supergroup wallpaper, if the settings carry one.</summary>
    public static string? EmoticonOf(MyTelegram.Schema.IWallPaperSettings? settings)
    {
        var emoticon = (settings as MyTelegram.Schema.TWallPaperSettings)?.Emoticon;

        return string.IsNullOrEmpty(emoticon) ? null : emoticon;
    }

    private static bool NamesNothing(MyTelegram.Schema.TWallPaperSettings settings)
    {
        return !settings.Blur &&
               !settings.Motion &&
               !settings.BackgroundColor.HasValue &&
               !settings.SecondBackgroundColor.HasValue &&
               !settings.ThirdBackgroundColor.HasValue &&
               !settings.FourthBackgroundColor.HasValue &&
               !settings.Intensity.HasValue &&
               !settings.Rotation.HasValue &&
               string.IsNullOrEmpty(settings.Emoticon);
    }

    private static void AddIfSet(BsonDocument doc, string name, int? value)
    {
        if (value.HasValue)
        {
            doc.Add(name, value.Value);
        }
    }

    private static int? GetInt32(BsonDocument doc, string name)
    {
        return doc.Contains(name) && !doc[name].IsBsonNull ? doc[name].ToInt32() : null;
    }

    private static string? GetString(BsonDocument doc, string name)
    {
        return doc.Contains(name) && !doc[name].IsBsonNull ? doc[name].AsString : null;
    }
}
