namespace MyTelegram.Messenger.Services.Caching;

/// <summary>
/// Cache helper for the <c>auth.importWebTokenAuthorization</c> flow. Keyed by the
/// <c>WebAuthToken</c> string, it resolves a token to a <see cref="WebTokenCacheItem"/>
/// (the authorized user and the api id the token was issued for), following the
/// <c>ICacheHelper&lt;string, CacheLoginToken&gt;</c> pattern used by the QR-login handlers.
/// </summary>
public interface IWebTokenAuthCacheHelper : ICacheHelper<string, WebTokenCacheItem>
{
}
