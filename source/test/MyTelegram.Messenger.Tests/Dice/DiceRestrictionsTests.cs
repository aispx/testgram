using MongoDB.Bson;
using MyTelegram.Messenger.Services.Dice;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Stickers;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Dice;

/// <summary>
/// Feature: what the server does with a <a href="https://corefork.telegram.org/api/dice">dice</a> beyond
/// rolling it — which sticker set backs it, where it is not allowed to go, and which banned right governs it.
///
/// <para>
/// None of these are cosmetic. A dice is the one media whose content the server mints at send time, so every
/// path that converts an <c>InputMedia</c> outside of a send would either roll a value nobody asked for or
/// roll a second one over a message that already has an outcome. And the right is <c>send_stickers</c>
/// rather than <c>send_games</c>: TDLib refuses a dice with "Not enough rights to send dice to the chat"
/// under <c>can_send_stickers</c> and Android gates the send on <c>canSendStickers</c>, so under the wrong
/// right a client offers a dice the server refuses, or refuses one the server would have taken.
/// </para>
/// </summary>
public class DiceRestrictionsTests
{
    private static ChatBannedRights Rights(bool sendStickers = false, bool sendGames = false)
    {
        var rights = ChatBannedRights.CreateDefaultBannedRights();
        rights.SendStickers = sendStickers;
        rights.SendGames = sendGames;

        return rights;
    }

    [Fact]
    public void A_dice_is_governed_by_send_stickers_not_send_games()
    {
        var dice = new TMessageMediaDice { Emoticon = "🎲", Value = 3 };

        MessageAppService
            .GetMediaBannedRightsError(dice, Rights(sendStickers: true))
            ?.Message
            .ShouldBe("CHAT_SEND_STICKERS_FORBIDDEN");

        // A chat that bans games but allows stickers still takes a dice — every client offers it there.
        MessageAppService
            .GetMediaBannedRightsError(dice, Rights(sendGames: true))
            .ShouldBeNull();

        MessageAppService
            .GetMediaBannedRightsError(dice, Rights())
            .ShouldBeNull();
    }

    [Fact]
    public void A_game_keeps_its_own_right()
    {
        var game = new TMessageMediaGame();

        MessageAppService
            .GetMediaBannedRightsError(game, Rights(sendGames: true))
            ?.Message
            .ShouldBe("CHAT_SEND_GAME_FORBIDDEN");

        MessageAppService
            .GetMediaBannedRightsError(game, Rights(sendStickers: true))
            .ShouldBeNull();
    }

    [Fact]
    public void Both_dice_forms_are_refused_where_a_dice_may_not_go()
    {
        // editMessage / sendMultiMedia / saveDraft / uploadMedia all route through this guard.
        Should.Throw<RpcException>(() => DiceMediaGuard.ThrowIfDice(new TInputMediaDice { Emoticon = "🎲" }))
            .RpcError.Message.ShouldBe("MEDIA_INVALID");

        Should.Throw<RpcException>(() => DiceMediaGuard.ThrowIfDice(new TInputMediaStakeDice
            {
                GameHash = "hash",
                TonAmount = 1_000_000_000,
                ClientSeed = new byte[32]
            }))
            .RpcError.Message.ShouldBe("MEDIA_INVALID");

        DiceMediaGuard.IsDice(new TInputMediaEmpty()).ShouldBeFalse();
        DiceMediaGuard.IsDice(null).ShouldBeFalse();
        DiceMediaGuard.ThrowIfDice(null);
    }

    /// <summary>
    /// Every dice emoji has to resolve to its own set. A miss answers <c>STICKERSET_INVALID</c>, and the
    /// client silently keeps the static system glyph instead of the animation.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Every_dice_emoji_resolves_to_its_own_set()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var collection = mongo.Database.GetCollection<BsonDocument>(StickerSetStore.CollectionName);

        var setId = 1L;
        foreach (var shortName in DiceEmojiHelper.All.Select(p => p.ShortName).Distinct())
        {
            await collection.InsertOneAsync(new BsonDocument
            {
                { "_id", $"stickersetreadmodel-{setId}" },
                { "StickerSetId", setId },
                { "ShortName", shortName },
                { "Title", shortName }
            });
            setId++;
        }

        var store = new StickerSetStore(mongo.Database);

        foreach (var dice in DiceEmojiHelper.All)
        {
            var lookup = await store.FindAsync(new TInputStickerSetDice { Emoticon = dice.Emoticon });

            lookup.Set.ShouldNotBeNull();
            lookup.Set!["ShortName"].AsString.ShouldBe(dice.ShortName);
            // The emoticon is threaded through the lookup because the pack reader and the hash helper both
            // need it: a dice set seeded without packs of its own is indexed under the emoji that was asked
            // for.
            lookup.Emoticon.ShouldBe(dice.Emoticon);
        }

        (await store.FindAsync(new TInputStickerSetDice { Emoticon = "🍆" })).Set.ShouldBeNull();
    }

    /// <summary>
    /// A dice set that was seeded without packs is still usable: the one emoji that was asked for indexes
    /// every animation in it. Telegram's own dice sets do carry keycap packs, so this is the fallback for a
    /// catalogue row written without them.
    /// </summary>
    [Fact]
    public void A_dice_set_without_packs_is_indexed_under_the_requested_emoji()
    {
        var document = new BsonDocument
        {
            { "StickerSetId", 1L },
            { "DocumentIds", new BsonArray { 10L, 11L, 12L } },
            { "Packs", new BsonArray() }
        };

        var packs = StickerSetPackReader.ReadPacks(document, "🎲");

        packs.Count.ShouldBe(1);
        ((TStickerPack)packs[0]).Emoticon.ShouldBe("🎲");
        ((TStickerPack)packs[0]).Documents.ShouldBe([10L, 11L, 12L]);
    }

    /// <summary>
    /// Real keycap packs must win over that fallback, because they are what tdesktop reads to pick the
    /// animation for a value (<c>#⃣</c> is the idle frame, <c>1⃣</c>..<c>6⃣</c> the outcomes).
    /// </summary>
    [Fact]
    public void Stored_keycap_packs_win_over_the_fallback()
    {
        var document = new BsonDocument
        {
            { "StickerSetId", 1L },
            { "DocumentIds", new BsonArray { 10L, 11L } },
            {
                "Packs", new BsonArray
                {
                    new BsonDocument { { "Emoticon", "#⃣" }, { "Documents", new BsonArray { 10L } } },
                    new BsonDocument { { "Emoticon", "1⃣" }, { "Documents", new BsonArray { 11L } } }
                }
            }
        };

        var packs = StickerSetPackReader.ReadPacks(document, "🎲");

        packs.ConvertAll(p => ((TStickerPack)p).Emoticon).ShouldBe(["#⃣", "1⃣"]);
    }
}
