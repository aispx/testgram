namespace MyTelegram.Messenger.Extensions;

public static class AppConfigExtensions
{
    /// <summary>
    /// Reads a numeric app config value (see <c>help.getAppConfig</c>) so handlers enforce the very
    /// same limits they advertise to clients instead of hardcoding them.
    /// </summary>
    public static int GetInt32Value(this IAppConfigHelper appConfigHelper, string key, int defaultValue)
    {
        if (appConfigHelper.GetAppConfig() is not TJsonObject jsonObject)
        {
            return defaultValue;
        }

        var item = jsonObject.Value.FirstOrDefault(p => p is TJsonObjectValue { } value && value.Key == key);
        if (item is TJsonObjectValue { Value: TJsonNumber number })
        {
            return (int)number.Value;
        }

        return defaultValue;
    }

    /// <summary>
    /// Reads an app config value that holds an array of strings, such as <c>pending_suggestions</c>.
    /// </summary>
    public static List<string> GetStringListValue(this IAppConfigHelper appConfigHelper, string key)
    {
        if (appConfigHelper.GetAppConfig() is not TJsonObject jsonObject)
        {
            return [];
        }

        var item = jsonObject.Value.FirstOrDefault(p => p is TJsonObjectValue value && value.Key == key);
        if (item is not TJsonObjectValue { Value: TJsonArray array })
        {
            return [];
        }

        return [.. array.Value.OfType<TJsonString>().Select(p => p.Value).Where(p => !string.IsNullOrEmpty(p))];
    }

    /// <summary>
    /// Reads an app config value that holds a 64-bit ID. Such IDs are advertised as strings, since
    /// JSON numbers cannot carry them losslessly.
    /// </summary>
    public static long? GetInt64Value(this IAppConfigHelper appConfigHelper, string key)
    {
        if (appConfigHelper.GetAppConfig() is not TJsonObject jsonObject)
        {
            return null;
        }

        var item = jsonObject.Value.FirstOrDefault(p => p is TJsonObjectValue value && value.Key == key);

        return item switch
        {
            TJsonObjectValue { Value: TJsonString { Value: { Length: > 0 } text } }
                when long.TryParse(text, out var parsed) => parsed,
            TJsonObjectValue { Value: TJsonNumber number } => (long)number.Value,
            _ => null
        };
    }
}
