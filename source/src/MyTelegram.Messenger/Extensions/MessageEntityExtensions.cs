namespace MyTelegram.Messenger.Extensions;

public static class MessageEntityExtensions
{
    public static IMessageEntity? CreateMessageEntityBold(this string text, string subText)
    {
        var startIndex = text.IndexOf(subText, StringComparison.OrdinalIgnoreCase);
        if (startIndex != -1)
        {
            return new TMessageEntityBold
            {
                Offset = startIndex,
                Length = subText.Length
            };
        }

        return null;
    }

    /// <summary>
    /// Marks a substring as a plain url. Service notifications are delivered as
    /// <c>updateServiceNotification</c>, and clients only make a link clickable when an entity says
    /// it is one.
    /// </summary>
    public static IMessageEntity? CreateMessageEntityUrl(this string text, string subText)
    {
        var startIndex = text.IndexOf(subText, StringComparison.OrdinalIgnoreCase);
        if (startIndex != -1)
        {
            return new TMessageEntityUrl
            {
                Offset = startIndex,
                Length = subText.Length
            };
        }

        return null;
    }
}