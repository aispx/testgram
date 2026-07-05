namespace MyTelegram.Messenger.Services.Caching;

/// <summary>
/// Default in-memory implementation of <see cref="IWebTokenAuthCacheHelper"/>, reusing the
/// shared <see cref="CacheHelper{TKey,TValue}"/> storage just like the open-generic
/// <c>ICacheHelper&lt;string, CacheLoginToken&gt;</c> registration backing the QR-login handlers.
/// </summary>
public class WebTokenAuthCacheHelper : CacheHelper<string, WebTokenCacheItem>, IWebTokenAuthCacheHelper
{
}
