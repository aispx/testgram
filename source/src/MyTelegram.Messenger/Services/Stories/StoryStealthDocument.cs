using MongoDB.Bson.Serialization.Attributes;

namespace MyTelegram.Messenger.Services.Stories;

/// <summary>
/// Per-user <a href="https://corefork.telegram.org/api/stories#stealth-mode">stealth mode</a> state.
/// Collection: <c>story_stealth_modes</c>, one document per user keyed by user id.
/// </summary>
public class StoryStealthDocument
{
    [BsonId]
    public long UserId { get; set; }

    /// <summary>Unix time until which views by this user are not recorded; null when never enabled.</summary>
    public int? ActiveUntilDate { get; set; }

    /// <summary>Unix time before which stealth mode cannot be enabled again.</summary>
    public int? CooldownUntilDate { get; set; }

    public bool IsActive(long currentUnixTime)
    {
        return ActiveUntilDate.HasValue && ActiveUntilDate.Value > currentUnixTime;
    }

    public bool IsOnCooldown(long currentUnixTime)
    {
        return CooldownUntilDate.HasValue && CooldownUntilDate.Value > currentUnixTime;
    }
}
