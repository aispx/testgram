namespace MyTelegram.Messenger.Services.Entities;

/// <summary>
/// Text length limits, counted in UTF-16 code units so that they agree with the units entity
/// offsets use. Without these checks a client can store a text no other client can address with an
/// <c>int</c> entity offset.
/// <para>
/// The values mirror the app config served by <c>AppConfigHelper.g.cs</c> (keys
/// <c>caption_length_limit_default</c> / <c>caption_length_limit_premium</c>); that file is
/// generated, so the constants are mirrored here rather than parsed back out of the generated JSON,
/// the same way <see cref="MyTelegram.Messenger.Helpers.TodoListHelper"/> does it.
/// </para>
/// </summary>
internal static class MessageLengthHelper
{
    /// <summary>Maximum length of a text message. Telegram has used 4096 since forever.</summary>
    public const int MessageLengthMax = 4096;

    /// <inheritdoc cref="MessageLengthMax"/>
    public const int CaptionLengthLimitDefault = 1024;

    /// <inheritdoc cref="MessageLengthMax"/>
    public const int CaptionLengthLimitPremium = 4096;

    /// <summary>Throws <c>MESSAGE_TOO_LONG</c> when a plain text message is over the limit.</summary>
    public static void ValidateMessage(string? message)
    {
        Validate(message, MessageLengthMax);
    }

    /// <summary>Throws <c>MESSAGE_TOO_LONG</c> when a media caption is over the limit.</summary>
    public static void ValidateCaption(string? caption, bool isPremium)
    {
        Validate(caption, isPremium ? CaptionLengthLimitPremium : CaptionLengthLimitDefault);
    }

    public static void Validate(string? text, int limit)
    {
        if (text != null && text.Length > limit)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }
    }
}
