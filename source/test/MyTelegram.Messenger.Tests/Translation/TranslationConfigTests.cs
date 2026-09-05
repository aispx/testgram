using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Translation;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Translation;

/// <summary>
/// Feature: the two <c>help.getAppConfig</c> keys that decide whether a client draws the translation UI
/// at all, and the per-account, per-peer flag behind <c>messages.togglePeerTranslations</c>.
///
/// <para>An absent key is not a neutral default here. iOS
/// (<c>AccountContext.swift</c>: <c>data["translations_manual_enabled"] as? String ?? "disabled"</c>),
/// Unigram (<c>ClientService.cs</c>: <c>GetNamedString("translations_manual_enabled", "disabled")</c>) and
/// tweb (<c>usePeerTranslation.ts</c>, which requires <c>=== 'enabled'</c>) all read a missing key as
/// <c>disabled</c> and then render nothing — a whole feature silently absent on three clients. Real
/// Telegram serves <c>"enabled"</c> for both, as a <c>jsonString</c>: Android and tweb type-check the
/// value and ignore a number.</para>
/// </summary>
public class TranslationConfigTests
{
    private static IJSONValue? ConfigValue(string key)
    {
        return ((TJsonObject)new AppConfigHelper().GetAppConfig()).Value
            .OfType<TJsonObjectValue>()
            .FirstOrDefault(p => p.Key == key)
            ?.Value;
    }

    [Theory]
    [InlineData("translations_manual_enabled")]
    [InlineData("translations_auto_enabled")]
    public void Both_translation_switches_are_advertised_as_enabled_strings(string key)
    {
        var value = ConfigValue(key);

        value.ShouldNotBeNull($"{key} is absent, which iOS, Unigram and tweb read as \"disabled\"");
        value.ShouldBeOfType<TJsonString>().Value.ShouldBe("enabled");
    }

    /// <summary>
    /// The boost level a channel needs before <c>channels.toggleAutotranslation</c> will accept it. The
    /// handler enforces the same number it advertises; a mismatch produces a refusal that contradicts
    /// what the client was told.
    /// </summary>
    [Fact]
    public void The_channel_autotranslation_boost_level_is_advertised()
    {
        ConfigValue("channel_autotranslation_level_min")
            .ShouldBeOfType<TJsonNumber>().Value.ShouldBe(3);
    }
}

/// <summary>
/// Feature: storing "do not offer to translate this chat", which is read back as
/// <c>translations_disabled</c> on <c>userFull</c>/<c>chatFull</c>/<c>channelFull</c>.
/// </summary>
public class PeerTranslationSettingsStoreTests
{
    private const long UserId = 2_010_001;
    private const long OtherUserId = 2_010_002;

    [RequiresMongoDbFact]
    public async Task A_peer_is_not_hidden_until_it_is_toggled()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new PeerTranslationSettingsStore(mongo.Database);
        var peer = new Peer(PeerType.User, 3_000_001);

        (await store.IsDisabledAsync(UserId, peer)).ShouldBeFalse();

        await store.SetAsync(UserId, peer, true);
        (await store.IsDisabledAsync(UserId, peer)).ShouldBeTrue();

        await store.SetAsync(UserId, peer, false);
        (await store.IsDisabledAsync(UserId, peer)).ShouldBeFalse();
    }

    /// <summary>
    /// Dismissing the popup is one account's decision. Storing it against the peer instead would hide
    /// the popup for everybody who talks to that user.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task The_flag_belongs_to_the_caller_not_to_the_peer()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new PeerTranslationSettingsStore(mongo.Database);
        var peer = new Peer(PeerType.Channel, 1_500_001);

        await store.SetAsync(UserId, peer, true);

        (await store.IsDisabledAsync(UserId, peer)).ShouldBeTrue();
        (await store.IsDisabledAsync(OtherUserId, peer)).ShouldBeFalse();
    }

    /// <summary>
    /// Two peers of the same account are independent — a row id that dropped the peer type would let a
    /// channel and a user with the same numeric id collide.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Peers_of_the_same_account_do_not_share_a_row()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new PeerTranslationSettingsStore(mongo.Database);
        var user = new Peer(PeerType.User, 4_000_001);
        var channel = new Peer(PeerType.Channel, 4_000_001);

        await store.SetAsync(UserId, user, true);

        (await store.IsDisabledAsync(UserId, user)).ShouldBeTrue();
        (await store.IsDisabledAsync(UserId, channel)).ShouldBeFalse();
    }

    /// <summary>
    /// <c>inputPeerSelf</c> normalises to <see cref="PeerType.Self"/>, which is the peer the read path in
    /// <c>users.getFullUser</c> resolves too. Saved Messages addressed as a plain user would be written to
    /// one row and read from another — the trap <c>account.getNotifySettings</c> fell into.
    /// </summary>
    [RequiresMongoDbFact]
    public async Task Saved_messages_is_its_own_peer()
    {
        using var mongo = EmbeddedMongoServer.Start();
        var store = new PeerTranslationSettingsStore(mongo.Database);
        var self = new Peer(PeerType.Self, UserId);

        await store.SetAsync(UserId, self, true);

        (await store.IsDisabledAsync(UserId, self)).ShouldBeTrue();
        (await store.IsDisabledAsync(UserId, new Peer(PeerType.User, UserId))).ShouldBeFalse();
    }
}
