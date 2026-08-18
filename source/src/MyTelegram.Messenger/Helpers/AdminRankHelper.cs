using System.Globalization;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Validation of the custom admin rank (title) shown next to an admin in a supergroup.
/// See https://corefork.telegram.org/method/channels.editAdmin
/// </summary>
public static class AdminRankHelper
{
    /// <summary>
    /// The client refuses anything longer than this and so does the server.
    /// </summary>
    public const int MaxRankLength = 16;

    /// <summary>
    /// Throws ADMIN_RANK_INVALID for a rank that is too long and ADMIN_RANK_EMOJI_NOT_ALLOWED when it
    /// contains an emoji. An empty or absent rank is fine — it just means "no custom title".
    /// </summary>
    public static void ValidateOrThrow(string? rank)
    {
        if (string.IsNullOrEmpty(rank))
        {
            return;
        }

        if (rank.Length > MaxRankLength)
        {
            RpcErrors.RpcErrors400.AdminRankInvalid.ThrowRpcError();
        }

        if (ContainsEmoji(rank))
        {
            RpcErrors.RpcErrors400.AdminRankEmojiNotAllowed.ThrowRpcError();
        }
    }

    /// <summary>
    /// Emoji live outside the Basic Multilingual Plane (surrogate pairs) or in the symbol ranges that
    /// .NET reports as <see cref="UnicodeCategory.OtherSymbol"/>, which also covers the older BMP
    /// emoji such as ☎ or ⚽.
    /// </summary>
    private static bool ContainsEmoji(string rank)
    {
        for (var i = 0; i < rank.Length; i++)
        {
            var c = rank[i];
            if (char.IsSurrogate(c))
            {
                return true;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherSymbol)
            {
                return true;
            }
        }

        return false;
    }
}
