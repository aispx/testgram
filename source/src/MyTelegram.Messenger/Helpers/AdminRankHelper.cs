using System.Globalization;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Validation of the custom rank (tag) shown next to a member in a supergroup, and of the rights
/// needed to change it. See https://corefork.telegram.org/api/rank
/// </summary>
public static class AdminRankHelper
{
    /// <summary>
    /// The client refuses anything longer than this and so does the server.
    /// </summary>
    public const int MaxRankLength = 16;

    /// <summary>
    /// May a member without the <c>manage_ranks</c> admin right change their own tag?
    /// Per https://corefork.telegram.org/api/rank it is allowed when the chat's default rights
    /// <i>or</i> the member's own banned rights permit <c>edit_rank</c>.
    /// A chat whose default rights were never configured counts as "not permitted": the tag stays
    /// an admin-granted thing until the owner opens the right explicitly.
    /// </summary>
    public static bool CanEditOwnRank(ChatBannedRights? defaultBannedRights,
        ChatBannedRights? memberBannedRights)
    {
        return defaultBannedRights is { EditRank: false } || memberBannedRights is { EditRank: false };
    }

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
