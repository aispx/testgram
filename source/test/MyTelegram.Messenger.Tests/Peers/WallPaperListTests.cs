using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Messenger.Services.WallPapers;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Peers;

/// <summary>
/// Feature: the wallpaper list — <c>account.getWallPapers</c> and the three methods that change it.
///
/// <para>The list is <b>per account</b>: "The API keeps a list of wallpapers that the user can set as chat
/// background, including some preinstalled ones … To remove a wallpaper (including preinstalled wallpapers)
/// from the list use account.saveWallPaper with unsave=true". It used to be the whole catalogue for
/// everybody, with <c>user_wallpapers</c> written and never read, so removing a wallpaper and resetting the
/// list were both invisible to every client.</para>
///
/// <para>The <c>hash</c> is the client's, computed by the client, and it used to be
/// <c>System.HashCode</c> — randomly seeded per process. See
/// <a href="https://corefork.telegram.org/api/wallpapers#installing-wallpapers">wallpapers »</a>.</para>
/// </summary>
public class WallPaperListTests
{
    private const long UserId = 2_000_001;
    private const long OtherUserId = 2_000_002;

    [RequiresMongoDbFact]
    public async Task The_hash_is_the_number_Android_computes_from_the_ids_it_received()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22, 33);

        var response = await GetWallPapersAsync(mongo.Database, hash: 0);

        var wallPapers = response.ShouldBeOfType<MyTelegram.Schema.Account.TWallPapers>();
        var ids = wallPapers.Wallpapers.Select(IdOf).ToList();
        ids.ShouldBe([11L, 22L, 33L]);
        wallPapers.Hash.ShouldBe(AndroidCalcHash(ids));
    }

    [RequiresMongoDbFact]
    public async Task Quoting_the_returned_hash_answers_wallPapersNotModified()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);
        var first = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);

        var second = await GetWallPapersAsync(mongo.Database, first.Hash);

        second.ShouldBeOfType<MyTelegram.Schema.Account.TWallPapersNotModified>();
    }

    /// <summary>
    /// An empty list has to arrive as an empty list. Answering <c>wallPapersNotModified</c>, as this used
    /// to when the catalogue was empty, leaves a client holding a stale copy it can never clear.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task An_empty_list_is_an_empty_list_and_not_notModified()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var response = await GetWallPapersAsync(mongo.Database, hash: 0);

        response.ShouldBeOfType<MyTelegram.Schema.Account.TWallPapers>().Wallpapers.Count.ShouldBe(0);
    }

    [RequiresMongoDbFact]
    public async Task Unsaving_a_preinstalled_wallpaper_keeps_it_out_of_the_next_list()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);

        await SaveWallPaperAsync(mongo.Database, 22, unsave: true);

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        wallPapers.Wallpapers.Select(IdOf).ShouldBe([11L]);
    }

    [RequiresMongoDbFact]
    public async Task Resetting_reinstalls_a_removed_preinstalled_wallpaper()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);
        await SaveWallPaperAsync(mongo.Database, 22, unsave: true);

        await ResetWallPapersAsync(mongo.Database);

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        wallPapers.Wallpapers.Select(IdOf).ShouldBe([11L, 22L]);
    }

    /// <summary>One user's list is not the other's.</summary>
    [RequiresMongoDbFact]
    public async Task Removing_a_wallpaper_only_affects_the_caller()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);
        await SaveWallPaperAsync(mongo.Database, 22, unsave: true);

        var other = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0,
            userId: OtherUserId);

        other.Wallpapers.Select(IdOf).ShouldBe([11L, 22L]);
    }

    /// <summary>A wallpaper the user saved sits above the preinstalled ones, newest first.</summary>
    [RequiresMongoDbFact]
    public async Task A_saved_wallpaper_comes_before_the_preinstalled_ones()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);
        await SeedWallPaperAsync(mongo.Database, 99, isDefault: false);

        await SaveWallPaperAsync(mongo.Database, 99, unsave: false);

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        wallPapers.Wallpapers.Select(IdOf).ShouldBe([99L, 11L, 22L]);
    }

    /// <summary>
    /// The settings passed to <c>saveWallPaper</c> are the user's own choice of blur and motion, and they
    /// have to come back with the wallpaper or the next device renders it unblurred.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_settings_a_user_saved_override_the_catalogue_ones()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database, 11, isDefault: true,
            settings: new BsonDocument { { "BackgroundColor", 111 } });

        await SaveWallPaperAsync(mongo.Database, 11, unsave: false,
            settings: new TWallPaperSettings { Blur = true, BackgroundColor = 222 });

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        var settings = wallPapers.Wallpapers.Single()
            .ShouldBeOfType<TWallPaperNoFile>().Settings.ShouldBeOfType<TWallPaperSettings>();
        settings.Blur.ShouldBeTrue();
        settings.BackgroundColor.ShouldBe(222);
    }

    /// <summary>
    /// "Fill wallpapers cannot be saved to the server … clients should install and keep track of them only
    /// locally". A client generates one with <c>id = 0</c>, and both methods used to accept it and write a
    /// list entry pointing at nothing.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_locally_generated_fill_wallpaper_cannot_be_saved()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11);

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(
            CreateHandler(mongo.Database, "Account.SaveWallPaperHandler"),
            new MyTelegram.Schema.Account.RequestSaveWallPaper
            {
                Wallpaper = new TInputWallPaperNoFile { Id = 0 },
                Unsave = false
            }));

        exception.RpcError.Message.ShouldBe("WALLPAPER_INVALID");
    }

    /// <summary>
    /// A preinstalled fill wallpaper still has to be removable, and Android addresses it exactly this way —
    /// <c>inputWallPaperNoFile{id}</c> with <c>unsave</c> (<c>WallpapersListActivity</c>). Refusing the
    /// constructor outright would break the documented "including preinstalled wallpapers" case.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_preinstalled_fill_wallpaper_can_be_removed_as_inputWallPaperNoFile()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22);

        await InvokeAsync(CreateHandler(mongo.Database, "Account.SaveWallPaperHandler"),
            new MyTelegram.Schema.Account.RequestSaveWallPaper
            {
                Wallpaper = new TInputWallPaperNoFile { Id = 22 },
                Unsave = true
            });

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        wallPapers.Wallpapers.Select(IdOf).ShouldBe([11L]);
    }

    [RequiresMongoDbFact]
    public async Task Installing_a_wallpaper_that_does_not_exist_is_WALLPAPER_INVALID()
    {
        using var mongo = EmbeddedMongoServer.Start();

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(
            CreateHandler(mongo.Database, "Account.InstallWallPaperHandler"),
            new MyTelegram.Schema.Account.RequestInstallWallPaper
            {
                Wallpaper = new TInputWallPaper { Id = 4242, AccessHash = 1 }
            }));

        exception.RpcError.Message.ShouldBe("WALLPAPER_INVALID");
    }

    /// <summary>Installing also saves: "calling this method will also automatically save the wallpaper".</summary>
    [RequiresMongoDbFact]
    public async Task Installing_a_wallpaper_saves_it()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedWallPaperAsync(mongo.Database, 99, isDefault: false);

        await InvokeAsync(CreateHandler(mongo.Database, "Account.InstallWallPaperHandler"),
            new MyTelegram.Schema.Account.RequestInstallWallPaper
            {
                Wallpaper = new TInputWallPaper { Id = 99, AccessHash = 1 }
            });

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);
        wallPapers.Wallpapers.Select(IdOf).ShouldBe([99L]);
    }

    /// <summary>
    /// <c>getMultiWallPapers</c> is positional — the caller matches the answer up by index — so the order
    /// of the request is the order of the response, and a missing entry is an error rather than a gap.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task getMultiWallPapers_answers_in_the_order_it_was_asked()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11, 22, 33);

        var result = (TVector<IWallPaper>)await InvokeAsync(
            CreateHandler(mongo.Database, "Account.GetMultiWallPapersHandler"),
            new MyTelegram.Schema.Account.RequestGetMultiWallPapers
            {
                Wallpapers = new TVector<IInputWallPaper>(
                    new TInputWallPaper { Id = 33, AccessHash = 1 },
                    new TInputWallPaper { Id = 11, AccessHash = 1 })
            });

        result.Select(IdOf).ShouldBe([33L, 11L]);
    }

    [RequiresMongoDbFact]
    public async Task getMultiWallPapers_refuses_a_wallpaper_it_does_not_have()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11);

        var exception = await Should.ThrowAsync<RpcException>(() => InvokeAsync(
            CreateHandler(mongo.Database, "Account.GetMultiWallPapersHandler"),
            new MyTelegram.Schema.Account.RequestGetMultiWallPapers
            {
                Wallpapers = new TVector<IInputWallPaper>(
                    new TInputWallPaper { Id = 11, AccessHash = 1 },
                    new TInputWallPaper { Id = 4242, AccessHash = 1 })
            }));

        exception.RpcError.Message.ShouldBe("WALLPAPER_INVALID");
    }

    /// <summary>
    /// A wallpaper whose document row is missing is left out rather than served as media no client can
    /// load — which is the state every image wallpaper imported from real Telegram was in.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_wallpaper_whose_document_is_missing_is_left_out()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await SeedDefaultsAsync(mongo.Database, 11);
        await SeedWallPaperAsync(mongo.Database, 22, isDefault: true, documentId: 777);

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);

        wallPapers.Wallpapers.Select(IdOf).ShouldBe([11L]);
    }

    /// <summary>
    /// Being in the starting list and carrying the <c>default</c> flag are two different things: real
    /// Telegram serves 83 wallpapers of which 76 are flagged <c>default</c> (measured), so the flag cannot
    /// be the list predicate.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task A_listed_wallpaper_is_served_even_without_the_default_flag()
    {
        using var mongo = EmbeddedMongoServer.Start();
        await mongo.Database.GetCollection<BsonDocument>("wallpapers").InsertOneAsync(new BsonDocument
        {
            { "WallpaperId", 11L }, { "Slug", "slug-11" }, { "DocumentId", 0L },
            { "IsDefault", false }, { "Listed", true }, { "Order", 0 }
        });

        var wallPapers = (MyTelegram.Schema.Account.TWallPapers)await GetWallPapersAsync(mongo.Database, hash: 0);

        wallPapers.Wallpapers.Single().ShouldBeOfType<TWallPaperNoFile>().Default.ShouldBeFalse();
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    /// <summary>
    /// An independent port of Android's <c>MediaDataController.calcHash</c> — the
    /// <a href="https://corefork.telegram.org/api/offsets#hash-generation">documented</a> accumulator, with
    /// Java's <c>&gt;&gt;&gt;</c> written as C#'s <c>&gt;&gt;&gt;</c>. The point of the test is that the
    /// server reproduces this number, so it is spelled out here rather than delegated to the helper under
    /// test.
    /// </summary>
    private static long AndroidCalcHash(IEnumerable<long> ids)
    {
        var hash = 0L;

        foreach (var id in ids)
        {
            hash ^= hash >>> 21;
            hash ^= hash << 35;
            hash ^= hash >>> 4;
            hash += id;
        }

        return hash;
    }

    private static long IdOf(IWallPaper wallPaper)
    {
        return wallPaper switch
        {
            TWallPaper paper => paper.Id,
            TWallPaperNoFile noFile => noFile.Id,
            _ => 0
        };
    }

    private static async Task SeedDefaultsAsync(IMongoDatabase database, params long[] wallPaperIds)
    {
        for (var i = 0; i < wallPaperIds.Length; i++)
        {
            await SeedWallPaperAsync(database, wallPaperIds[i], isDefault: true, order: i);
        }
    }

    private static Task SeedWallPaperAsync(IMongoDatabase database, long wallPaperId, bool isDefault,
        long documentId = 0, BsonDocument? settings = null, int order = 0)
    {
        return database.GetCollection<BsonDocument>("wallpapers").InsertOneAsync(new BsonDocument
        {
            { "_id", $"wallpaper-{wallPaperId}" },
            { "WallpaperId", wallPaperId },
            { "AccessHash", 1L },
            { "Slug", $"slug-{wallPaperId}" },
            { "DocumentId", documentId },
            { "IsDefault", isDefault },
            { "Order", order },
            { "Settings", settings ?? (BsonValue)BsonNull.Value }
        });
    }

    private static async Task<MyTelegram.Schema.Account.IWallPapers> GetWallPapersAsync(IMongoDatabase database,
        long hash, long userId = UserId)
    {
        return (MyTelegram.Schema.Account.IWallPapers)await InvokeAsync(
            CreateHandler(database, "Account.GetWallPapersHandler"),
            new MyTelegram.Schema.Account.RequestGetWallPapers { Hash = hash }, userId);
    }

    private static Task<object> SaveWallPaperAsync(IMongoDatabase database, long wallPaperId, bool unsave,
        IWallPaperSettings? settings = null)
    {
        return InvokeAsync(CreateHandler(database, "Account.SaveWallPaperHandler"),
            new MyTelegram.Schema.Account.RequestSaveWallPaper
            {
                Wallpaper = new TInputWallPaper { Id = wallPaperId, AccessHash = 1 },
                Unsave = unsave,
                Settings = settings!
            });
    }

    private static Task<object> ResetWallPapersAsync(IMongoDatabase database)
    {
        return InvokeAsync(CreateHandler(database, "Account.ResetWallPapersHandler"),
            new MyTelegram.Schema.Account.RequestResetWallPapers());
    }

    /// <summary>
    /// The handlers are <c>internal</c>, so they are built by reflection — and their dependencies are
    /// matched by parameter type, which keeps this fixture working when one of them grows another one.
    /// </summary>
    private static object CreateHandler(IMongoDatabase database, string name)
    {
        var type = typeof(WallPaperCatalog).Assembly.GetType(
            $"MyTelegram.Messenger.Handlers.LatestLayer.{name}", throwOnError: true)!;

        var catalog = new WallPaperCatalog(database, TestFileReferences.Helper,
            NullLogger<WallPaperCatalog>.Instance);
        var store = new UserWallPaperStore(database, catalog);

        var constructor = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();

        var args = constructor.GetParameters().Select(object? (p) =>
        {
            if (p.ParameterType == typeof(IWallPaperCatalog))
            {
                return catalog;
            }

            if (p.ParameterType == typeof(IUserWallPaperStore))
            {
                return store;
            }

            if (p.ParameterType == typeof(IMongoDatabase))
            {
                return database;
            }

            throw new NotSupportedException($"No fixture for {p.ParameterType.Name}");
        }).ToArray();

        return constructor.Invoke(args);
    }

    private static async Task<object> InvokeAsync(object handler, object request, long userId = UserId)
    {
        var input = new Mock<IRequestInput>(MockBehavior.Loose);
        input.SetupGet(p => p.UserId).Returns(userId);

        var method = handler.GetType().GetMethod("HandleCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task task;
        try
        {
            task = (Task)method.Invoke(handler, [input.Object, request])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        await task;

        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
}
