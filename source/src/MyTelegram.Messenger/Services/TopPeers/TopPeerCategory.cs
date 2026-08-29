namespace MyTelegram.Messenger.Services.TopPeers;

/// <summary>
/// The <a href="https://corefork.telegram.org/api/top-rating">top peer</a> categories, declared in
/// the order tdlib's <c>TopDialogCategory</c> enum declares them.
///
/// <para>That order is a wire contract, not a detail. tdlib asks for every category at once and sends
/// back <c>get_vector_hash</c> over the peer ids of <b>all</b> of its cached categories concatenated in
/// enum order (<c>TopDialogManager::do_get_top_peers</c>), so a server that emits the categories in a
/// different order can never produce a hash that matches and <c>contacts.topPeersNotModified</c> can
/// never fire.</para>
/// </summary>
public enum TopPeerCategory
{
    Correspondents = 0,
    BotsPM = 1,
    BotsInline = 2,
    Groups = 3,
    Channels = 4,
    PhoneCalls = 5,
    ForwardUsers = 6,
    ForwardChats = 7,
    BotsApp = 8
}

/// <summary>Maps <see cref="TopPeerCategory"/> onto the TL <c>TopPeerCategory</c> constructors.</summary>
public static class TopPeerCategoryHelper
{
    /// <summary>
    /// The order categories are emitted in, and the order their peers are folded into the hash.
    /// Matches tdlib's enum exactly; <c>topPeerCategoryBotsGuestChat</c> is absent because layer 222
    /// does not carry it.
    /// </summary>
    public static readonly TopPeerCategory[] WireOrder =
    [
        TopPeerCategory.Correspondents,
        TopPeerCategory.BotsPM,
        TopPeerCategory.BotsInline,
        TopPeerCategory.Groups,
        TopPeerCategory.Channels,
        TopPeerCategory.PhoneCalls,
        TopPeerCategory.ForwardUsers,
        TopPeerCategory.ForwardChats,
        TopPeerCategory.BotsApp
    ];

    /// <summary>
    /// Categories whose rating is counted from explicitly recorded uses in
    /// <c>top_peer_usage</c> rather than derived from message history: picking an inline result,
    /// opening a mini app, finishing a call and forwarding are all things that leave no usable trace
    /// in <c>eventflow-messagereadmodel</c>.
    /// </summary>
    public static bool IsUsageTracked(TopPeerCategory category)
    {
        return category is TopPeerCategory.BotsInline
            or TopPeerCategory.BotsApp
            or TopPeerCategory.PhoneCalls
            or TopPeerCategory.ForwardUsers
            or TopPeerCategory.ForwardChats;
    }

    public static ITopPeerCategory ToTl(TopPeerCategory category)
    {
        return category switch
        {
            TopPeerCategory.Correspondents => new TTopPeerCategoryCorrespondents(),
            TopPeerCategory.BotsPM => new TTopPeerCategoryBotsPM(),
            TopPeerCategory.BotsInline => new TTopPeerCategoryBotsInline(),
            TopPeerCategory.Groups => new TTopPeerCategoryGroups(),
            TopPeerCategory.Channels => new TTopPeerCategoryChannels(),
            TopPeerCategory.PhoneCalls => new TTopPeerCategoryPhoneCalls(),
            TopPeerCategory.ForwardUsers => new TTopPeerCategoryForwardUsers(),
            TopPeerCategory.ForwardChats => new TTopPeerCategoryForwardChats(),
            TopPeerCategory.BotsApp => new TTopPeerCategoryBotsApp(),
            _ => new TTopPeerCategoryCorrespondents()
        };
    }

    /// <summary>
    /// Returns <c>null</c> for a constructor this layer does not model, which callers treat as
    /// "every category" rather than as an error.
    /// </summary>
    public static TopPeerCategory? FromTl(ITopPeerCategory? category)
    {
        return category switch
        {
            TTopPeerCategoryCorrespondents => TopPeerCategory.Correspondents,
            TTopPeerCategoryBotsPM => TopPeerCategory.BotsPM,
            TTopPeerCategoryBotsInline => TopPeerCategory.BotsInline,
            TTopPeerCategoryGroups => TopPeerCategory.Groups,
            TTopPeerCategoryChannels => TopPeerCategory.Channels,
            TTopPeerCategoryPhoneCalls => TopPeerCategory.PhoneCalls,
            TTopPeerCategoryForwardUsers => TopPeerCategory.ForwardUsers,
            TTopPeerCategoryForwardChats => TopPeerCategory.ForwardChats,
            TTopPeerCategoryBotsApp => TopPeerCategory.BotsApp,
            _ => null
        };
    }
}
