using MyTelegram.Messenger.Services.Stickers;
using MyTelegram.Messenger.Tests.Stats;

namespace MyTelegram.Messenger.Tests.Stickers;

/// <summary>
/// Feature: the installed / archived stickerset lists behind
/// <a href="https://corefork.telegram.org/api/stickers#installing-stickersets">messages.installStickerSet</a>
/// and friends.
///
/// <para>
/// Order is a correctness requirement rather than a presentation choice: clients render the panel in the
/// order they receive and compute the hash they send from that same sequence, so "newest install first" and
/// "a reorder sticks" are what make <c>allStickersNotModified</c> possible at all. Archiving is the other
/// half — an archived set stays installed but leaves the panel, and it is the only thing
/// <c>getArchivedStickers</c> has to report.
/// </para>
/// </summary>
public class InstalledStickerSetStoreTests
{
    private const long UserId = 2_000_001;
    private const long OtherUserId = 2_000_002;

    [RequiresMongoDbFact]
    public async Task Newly_installed_sets_come_first()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 333, StickerSetType.Regular, false);

        var installed = await store.GetAsync(UserId, StickerSetType.Regular, false);

        installed.ConvertAll(p => p.StickerSetId).ShouldBe([333L, 222L, 111L]);
    }

    /// <summary>
    /// Re-installing an archived set is how every client un-archives one — there is no separate method for
    /// it. Leaving the row untouched, as the handler used to, made the "unarchive" button do nothing.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Installing_an_archived_set_again_unarchives_it_and_moves_it_to_the_front()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, true);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L]);

        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([222L, 111L]);
        (await store.GetAsync(UserId, StickerSetType.Regular, true)).ShouldBeEmpty();
    }

    /// <summary>
    /// The install date is <c>stickerSet.installed_date</c>, which doubles as the "is it installed" flag, so
    /// it must survive an un-archive rather than being reset by it.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_install_date_is_kept_across_a_re_install()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        var first = (await store.GetAsync(UserId, StickerSetType.Regular, false)).Single().Date;

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);

        (await store.GetAsync(UserId, StickerSetType.Regular, false)).Single().Date.ShouldBe(first);
    }

    /// <summary>
    /// The three panels are fetched by three different methods, so a mask set must never surface in the
    /// sticker list — that separation is why the type is stored rather than derived at read time.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Each_kind_of_set_has_its_own_list()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Mask, false);
        await store.InstallAsync(UserId, 333, StickerSetType.CustomEmoji, false);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L]);
        (await store.GetAsync(UserId, StickerSetType.Mask, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([222L]);
        (await store.GetAsync(UserId, StickerSetType.CustomEmoji, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([333L]);
    }

    [RequiresMongoDbFact]
    public async Task Reordering_puts_the_sets_in_the_order_the_client_sent()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 333, StickerSetType.Regular, false);

        // The client sends the panel top first.
        await store.ReorderAsync(UserId, StickerSetType.Regular, [111, 333, 222]);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L, 333L, 222L]);
    }

    /// <summary>
    /// A set installed by another session while the reorder was in flight is not in the vector the client
    /// sent; it must end up below the reordered block rather than jumping to the top.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Reordering_does_not_promote_a_set_the_client_did_not_mention()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 999, StickerSetType.Regular, false);

        await store.ReorderAsync(UserId, StickerSetType.Regular, [111, 222]);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L, 222L, 999L]);
    }

    [RequiresMongoDbFact]
    public async Task Sending_a_sticker_can_move_its_set_to_the_top()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);

        (await store.MoveToTopAsync(UserId, 111)).ShouldBeTrue();

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L, 222L]);

        // Nothing to move for a set the user does not have, so the caller can skip the update it would push.
        (await store.MoveToTopAsync(UserId, 555)).ShouldBeFalse();
    }

    /// <summary>
    /// Past the limit the server archives the least recently used sets and reports them, which is what
    /// <c>messages.stickerSetInstallResultArchive</c> carries.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Installing_past_the_limit_archives_the_oldest_sets()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 333, StickerSetType.Regular, false);

        (await store.ArchiveOverflowAsync(UserId, StickerSetType.Regular, 2)).ShouldBe([111L]);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([333L, 222L]);
        (await store.GetAsync(UserId, StickerSetType.Regular, true))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L]);
        (await store.CountAsync(UserId, StickerSetType.Regular, true)).ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Uninstalling_reports_whether_anything_was_there()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);

        (await store.UninstallAsync(UserId, 111)).ShouldBeTrue();
        (await store.UninstallAsync(UserId, 111)).ShouldBeFalse();
        (await store.GetAsync(UserId, StickerSetType.Regular, false)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Archiving_reports_only_the_sets_it_actually_changed()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(UserId, 222, StickerSetType.Regular, true);

        // 222 is already archived and 555 is not installed at all; neither is a change worth an update.
        (await store.SetArchivedAsync(UserId, [111, 222, 555], true)).ShouldBe([111L]);
        (await store.SetArchivedAsync(UserId, [111], true)).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task One_users_panel_is_not_another_users_panel()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(OtherUserId, 222, StickerSetType.Regular, false);

        (await store.GetAsync(UserId, StickerSetType.Regular, false))
            .ConvertAll(p => p.StickerSetId).ShouldBe([111L]);
        (await store.GetOverlayAsync(UserId, [222])).ShouldBeEmpty();
    }

    /// <summary>
    /// Deleting a set has to remove it from everyone's panel, not just the creator's.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Removing_a_set_for_all_users_clears_every_panel()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new InstalledStickerSetStore(mongo.Database);

        await store.InstallAsync(UserId, 111, StickerSetType.Regular, false);
        await store.InstallAsync(OtherUserId, 111, StickerSetType.Regular, false);

        await store.RemoveForAllUsersAsync(111);

        (await store.GetAsync(UserId, StickerSetType.Regular, false)).ShouldBeEmpty();
        (await store.GetAsync(OtherUserId, StickerSetType.Regular, false)).ShouldBeEmpty();
    }
}
