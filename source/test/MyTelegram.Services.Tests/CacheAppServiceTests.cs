using MyTelegram.Messenger.Services.Caching;
using Shouldly;

namespace MyTelegram.Services.Tests;

public class CacheAppServiceTests
{
    [Fact]
    public async Task Remove_deletes_cached_contact_pair()
    {
        var cache = new CacheAppService();

        cache.Add(2010001, 2012001);
        (await cache.IsExistsAsync(2010001, 2012001)).ShouldBeTrue();

        cache.Remove(2010001, 2012001);

        (await cache.IsExistsAsync(2010001, 2012001)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cache_supports_concurrent_startup_load_and_reads()
    {
        var cache = new CacheAppService();

        var tasks = Enumerable.Range(0, Environment.ProcessorCount * 4).Select(worker => Task.Run(async () =>
        {
            for (var i = worker; i < 20_000; i += Environment.ProcessorCount * 4)
            {
                var selfUserId = i % 256;
                var targetUserId = i;

                cache.Add(selfUserId, targetUserId);
                _ = await cache.IsExistsAsync(selfUserId, targetUserId);
            }
        }));

        await Task.WhenAll(tasks);

        (await cache.IsExistsAsync(42, 42)).ShouldBeTrue();
    }
}
