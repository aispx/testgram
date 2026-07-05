namespace MyTelegram.Messenger.Services.Caching;

/// <summary>
/// Cache entry for the <c>auth.importWebTokenAuthorization</c> flow, keyed by the
/// <c>WebAuthToken</c> string. Mirrors the <see cref="CacheLoginToken"/> pattern used by the
/// QR-login handlers: the web page that authorizes a login populates the cache, and
/// <c>ImportWebTokenAuthorizationHandler</c> resolves the token back to a user.
/// </summary>
public record WebTokenCacheItem(long UserId, int ApiId);
