namespace MyTelegram.Domain;

public static class TelegramDeepLinkHelper
{
    public static string GetBusinessChatLink(string slug)
    {
        return $"https://t.me/m/{slug}";
    }

    public static string GetBoostLink(string? username, long channelId)
    {
        return !string.IsNullOrWhiteSpace(username)
            ? $"https://t.me/boost/{username}"
            : $"https://t.me/boost?c={channelId}";
    }

    public static string GetConferenceCallLink(string slug)
    {
        return $"https://t.me/call/{slug}";
    }

    public static string GetGroupCallInviteLink(string username, string? inviteHash, bool livestream)
    {
        var parameter = livestream ? "livestream" : "videochat";

        return string.IsNullOrWhiteSpace(inviteHash)
            ? $"https://t.me/{username}?{parameter}"
            : $"https://t.me/{username}?{parameter}={inviteHash}";
    }
}
