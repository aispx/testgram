using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Entities;

/// <summary>
/// Rejects entities a client must never be able to store. Offsets and lengths are counted in UTF-16
/// code units, which is exactly what a <see cref="string"/> index is in .NET, so no conversion is
/// needed — only the arithmetic has to be right.
/// See https://corefork.telegram.org/api/entities#entity-length
/// </summary>
internal static class MessageEntityValidator
{
    /// <summary>
    /// Maximum number of entities accepted on a single text. The official server answers
    /// <c>ENTITIES_TOO_LONG</c> past its own (undocumented) ceiling; 100 is the value tdlib assumes
    /// when it splits a message.
    /// </summary>
    public const int MaxEntities = 100;

    /// <summary>Maximum length of <c>messageEntityTextUrl.url</c>.</summary>
    public const int MaxUrlLength = 4096;

    /// <summary>Maximum length of <c>messageEntityPre.language</c>.</summary>
    public const int MaxLanguageLength = 64;

    /// <summary>
    /// Throws <c>ENTITY_BOUNDS_INVALID</c> / <c>ENTITIES_TOO_LONG</c> for anything that could not
    /// have been produced by a correct client. Entities the API does not accept as input
    /// (<c>messageEntityUnknown</c>, the diff entities) are not an error — the normaliser drops them.
    /// </summary>
    public static void Validate(string? text, IList<IMessageEntity>? entities)
    {
        if (entities == null || entities.Count == 0)
        {
            return;
        }

        if (entities.Count > MaxEntities)
        {
            RpcErrors.RpcErrors400.EntitiesTooLong.ThrowRpcError();
        }

        var length = text?.Length ?? 0;
        foreach (var entity in entities)
        {
            if (MessageEntityKinds.GetKind(entity) == MessageEntityKind.Dropped)
            {
                continue;
            }

            ValidateBounds(text, length, entity);
            ValidateArguments(entity);
        }
    }

    private static void ValidateBounds(string? text, int length, IMessageEntity entity)
    {
        if (entity.Offset < 0 || entity.Length <= 0 || entity.Offset > length - entity.Length)
        {
            RpcErrors.RpcErrors400.EntityBoundsInvalid.ThrowRpcError();
        }

        if (text == null)
        {
            return;
        }

        // A boundary inside a surrogate pair would make the client cut a codepoint in half.
        if (IsInsideSurrogatePair(text, entity.Offset) ||
            IsInsideSurrogatePair(text, entity.Offset + entity.Length))
        {
            RpcErrors.RpcErrors400.EntityBoundsInvalid.ThrowRpcError();
        }
    }

    private static bool IsInsideSurrogatePair(string text, int index)
    {
        return index > 0 && index < text.Length &&
               char.IsHighSurrogate(text[index - 1]) &&
               char.IsLowSurrogate(text[index]);
    }

    private static void ValidateArguments(IMessageEntity entity)
    {
        switch (entity)
        {
            case TMessageEntityTextUrl textUrl:
                if (string.IsNullOrEmpty(textUrl.Url) || textUrl.Url.Length > MaxUrlLength ||
                    !IsAllowedUrl(textUrl.Url))
                {
                    RpcErrors.RpcErrors400.EntityBoundsInvalid.ThrowRpcError();
                }

                break;

            case TMessageEntityPre pre when !string.IsNullOrEmpty(pre.Language):
                if (pre.Language.Length > MaxLanguageLength || !IsValidLanguageCode(pre.Language))
                {
                    RpcErrors.RpcErrors400.EntityBoundsInvalid.ThrowRpcError();
                }

                break;

            case TMessageEntityMentionName mentionName when mentionName.UserId <= 0:
                RpcErrors.RpcErrors400.EntityMentionUserInvalid.ThrowRpcError();
                break;
        }
    }

    /// <summary>
    /// Schemes a <c>textUrl</c> may point at. Anything else (<c>javascript:</c>, <c>data:</c>) would
    /// be handed to the client's in-app browser, so it is refused here rather than in each client.
    /// </summary>
    private static bool IsAllowedUrl(string url)
    {
        var separator = url.IndexOf(':');
        if (separator < 0)
        {
            // Schemeless links are resolved as http by every client.
            return !url.Contains(' ');
        }

        var scheme = url[..separator];

        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("tg", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase) ||
               scheme.Equals("tel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Mirrors tdlib's <c>is_valid_language_code</c>.</summary>
    private static bool IsValidLanguageCode(string language)
    {
        foreach (var c in language)
        {
            var isAllowed = c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' ||
                            c is '-' or '+' or '.' or '_' or '#';
            if (!isAllowed)
            {
                return false;
            }
        }

        return true;
    }
}
