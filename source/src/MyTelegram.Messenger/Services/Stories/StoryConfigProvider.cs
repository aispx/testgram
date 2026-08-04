using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Stories;

public interface IStoryConfigProvider
{
    int GetInt(string key, int defaultValue);
    string GetString(string key, string defaultValue);

    /// <summary>Max concurrently active (unexpired) stories, per <c>story_expiring_limit_*</c>.</summary>
    int GetExpiringLimit(bool isPremium);

    /// <summary>Max caption length, per <c>story_caption_length_limit_*</c>.</summary>
    int GetCaptionLengthLimit(bool isPremium);

    /// <summary>Max stories pinned to the top of a profile, per <c>stories_pinned_to_top_count_max</c>.</summary>
    int GetPinnedToTopMax();

    int GetStealthFuturePeriod();
    int GetStealthPastPeriod();
    int GetStealthCooldownPeriod();

    /// <summary>Whether styled caption entities are allowed for this user, per <c>stories_entities</c>.</summary>
    bool AreEntitiesAllowed(bool isPremium);
}

/// <summary>
/// Reads story-related limits out of the app config (<c>help.getAppConfig</c>) so handlers do not
/// hardcode them. Values come from the same source clients see, keeping server and client in agreement.
/// </summary>
public class StoryConfigProvider(IAppConfigHelper appConfigHelper) : IStoryConfigProvider, ITransientDependency
{
    public int GetInt(string key, int defaultValue)
    {
        return FindValue(key) switch
        {
            TJsonNumber number => (int)number.Value,
            _ => defaultValue
        };
    }

    public string GetString(string key, string defaultValue)
    {
        return FindValue(key) switch
        {
            TJsonString str => str.Value,
            _ => defaultValue
        };
    }

    public int GetExpiringLimit(bool isPremium)
    {
        return isPremium
            ? GetInt("story_expiring_limit_premium", 100)
            : GetInt("story_expiring_limit_default", 3);
    }

    public int GetCaptionLengthLimit(bool isPremium)
    {
        return isPremium
            ? GetInt("story_caption_length_limit_premium", 2048)
            : GetInt("story_caption_length_limit_default", 200);
    }

    public int GetPinnedToTopMax() => GetInt("stories_pinned_to_top_count_max", 3);

    public int GetStealthFuturePeriod() => GetInt("stories_stealth_future_period", 1500);

    public int GetStealthPastPeriod() => GetInt("stories_stealth_past_period", 300);

    public int GetStealthCooldownPeriod() => GetInt("stories_stealth_cooldown_period", 10800);

    public bool AreEntitiesAllowed(bool isPremium)
    {
        return GetString("stories_entities", "premium") switch
        {
            "enabled" => true,
            "premium" => isPremium,
            _ => false
        };
    }

    private IJSONValue? FindValue(string key)
    {
        if (appConfigHelper.GetAppConfig() is not TJsonObject jsonObject)
        {
            return null;
        }

        foreach (var item in jsonObject.Value)
        {
            if (item is TJsonObjectValue objectValue && objectValue.Key == key)
            {
                return objectValue.Value;
            }
        }

        return null;
    }
}
