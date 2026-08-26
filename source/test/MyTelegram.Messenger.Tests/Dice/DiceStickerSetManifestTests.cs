using System.IO;
using System.Text.Json;
using MyTelegram.Messenger.Services.Dice;

namespace MyTelegram.Messenger.Tests.Dice;

/// <summary>
/// Feature: the shape of the six seeded <a href="https://corefork.telegram.org/api/dice">dice</a> sticker
/// sets, as recorded in <c>scripts/stickers_manifest.json</c> — the input every re-seed replays into
/// <c>eventflow-stickersetreadmodel</c>.
///
/// <para>
/// Clients pick the animation for a value in two incompatible ways and the sets have to satisfy both at
/// once. TDLib indexes <c>documents</c> positionally (<c>StickersManager::get_dice_stickers</c>:
/// <c>sticker_ids_[value]</c>, with index 0 the idle preview), while tdesktop ignores the order and reads
/// the keycap packs instead (<c>DicePacks::applySet</c>: <c>#⃣</c> maps to 0 and <c>1⃣</c>..<c>6⃣</c> to
/// 1..6). Satisfying only one of them breaks the other silently — the wrong animation, or none, with no
/// error anywhere. A re-seed that reorders documents or drops the numeric packs is exactly the change that
/// would do it, so the invariant is pinned here rather than left to be noticed on a phone.
/// </para>
/// </summary>
public class DiceStickerSetManifestTests
{
    private static readonly string[] Keycaps = ["#⃣", "1⃣", "2⃣", "3⃣", "4⃣", "5⃣", "6⃣"];

    private sealed record ManifestSet(
        string Name,
        List<long> DocumentIds,
        List<(string Emoticon, List<long> Documents)> Packs);

    private static Dictionary<string, ManifestSet> LoadManifest()
    {
        var path = FindManifest()
                   ?? throw new FileNotFoundException(
                       "scripts/stickers_manifest.json was not found above the test output directory.");

        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);

        var sets = new Dictionary<string, ManifestSet>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString()!;
            var documentIds = entry.GetProperty("documents")
                .EnumerateArray()
                .Select(p => p.GetProperty("doc_id").GetInt64())
                .ToList();

            var packs = entry.GetProperty("packs")
                .EnumerateArray()
                .Select(p => (
                    Emoticon: p.GetProperty("emoticon").GetString()!,
                    Documents: p.GetProperty("documents").EnumerateArray().Select(d => d.GetInt64()).ToList()))
                .ToList();

            sets[name] = new ManifestSet(name, documentIds, packs);
        }

        return sets;
    }

    private static string? FindManifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "stickers_manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void Every_dice_set_is_in_the_manifest_with_the_document_count_its_range_needs()
    {
        var manifest = LoadManifest();

        foreach (var dice in DiceEmojiHelper.All)
        {
            manifest.ShouldContainKey(dice.ShortName);

            // One document per outcome plus the idle preview — or the slot machine's fixed 21.
            manifest[dice.ShortName].DocumentIds.Count.ShouldBe(DiceEmojiHelper.GetDocumentCount(dice));
        }
    }

    [Fact]
    public void Non_slot_dice_sets_agree_on_both_client_lookup_schemes()
    {
        var manifest = LoadManifest();

        foreach (var dice in DiceEmojiHelper.All.Where(p => p.Emoticon != DiceEmojiHelper.SlotMachineEmoticon))
        {
            var set = manifest[dice.ShortName];

            // The keycap packs tdesktop reads: one per outcome, plus the idle frame, in order.
            set.Packs.ConvertAll(p => p.Emoticon).ShouldBe(Keycaps[..(dice.MaxValue + 1)]);

            for (var index = 0; index <= dice.MaxValue; index++)
            {
                var pack = set.Packs[index];
                pack.Documents.Count.ShouldBe(1, $"set {set.Name} pack {pack.Emoticon}");

                // The position TDLib would index and the document the pack names have to be the same one.
                pack.Documents[0].ShouldBe(set.DocumentIds[index], $"set {set.Name} pack {pack.Emoticon}");
            }
        }
    }

    /// <summary>
    /// The slot machine is positional for both clients — tdesktop skips the pack pass for it entirely and
    /// numbers the documents as they arrive — so all that matters is that the 21 slots are distinct and in a
    /// fixed order: background variants 0-1, lever 2, then the three reels at 3-8, 9-14 and 15-20.
    /// </summary>
    [Fact]
    public void The_slot_machine_set_carries_twenty_one_distinct_documents()
    {
        var set = LoadManifest()["SlotMachineAnimated"];

        set.DocumentIds.Count.ShouldBe(DiceEmojiHelper.SlotMachineDocumentCount);
        set.DocumentIds.Distinct().Count().ShouldBe(DiceEmojiHelper.SlotMachineDocumentCount);
    }
}
