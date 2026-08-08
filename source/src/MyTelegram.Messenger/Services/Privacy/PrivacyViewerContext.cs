namespace MyTelegram.Messenger.Services.Privacy;

/// <summary>
/// Facts about the viewer that privacy rules beyond the classic contacts/users ones need in
/// order to be evaluated.
/// See https://corefork.telegram.org/api/privacy
/// </summary>
/// <remarks>
/// The original evaluator only knew the viewer's id and whether they were a contact, which is
/// enough for allowAll / allowContacts / allowUsers but not for the newer
/// <c>allowPremium</c>, <c>allowBots</c>, <c>allowCloseFriends</c> and
/// <c>allowChatParticipants</c> rules. Those rules were therefore rejected outright at
/// <c>account.setPrivacy</c> time. This record carries the missing facts; callers that cannot
/// supply them get <see cref="Unknown"/>, under which the affected rules deny access rather
/// than silently granting it.
/// </remarks>
/// <param name="IsPremium">Whether the viewer has a Telegram Premium subscription.</param>
/// <param name="IsBot">Whether the viewer is a bot.</param>
/// <param name="IsCloseFriend">Whether the target user lists the viewer as a close friend.</param>
/// <param name="ChatIds">Chats and channels the viewer participates in.</param>
public sealed record PrivacyViewerContext(
    bool IsPremium,
    bool IsBot,
    bool IsCloseFriend,
    IReadOnlySet<long> ChatIds)
{
    /// <summary>
    /// False on <see cref="Unknown"/>, i.e. when the caller could not supply the viewer facts.
    /// <para>
    /// The facts are only missing, never known-to-be-false, so an unmatched <c>Disallow*</c> rule
    /// cannot be read as "the viewer is not excluded". Treating it that way is what let a viewer
    /// excluded by <c>disallowChatParticipants</c> or <c>disallowBots</c> fall through to the
    /// <c>allowAll</c> rule alongside it and read the protected field anyway. Rules that depend on
    /// these facts therefore deny access while this is false — see
    /// <c>PrivacyHelper.IsAllowedByPrivacy</c>.
    /// </para>
    /// </summary>
    public bool FactsKnown { get; init; } = true;

    /// <summary>
    /// Used when the caller has no viewer details. Allow-rules that depend on the missing facts
    /// evaluate as "not matched", and fact-dependent disallow-rules deny outright, so the
    /// protected data stays hidden either way.
    /// </summary>
    public static readonly PrivacyViewerContext Unknown =
        new(false, false, false, new HashSet<long>()) { FactsKnown = false };
}
