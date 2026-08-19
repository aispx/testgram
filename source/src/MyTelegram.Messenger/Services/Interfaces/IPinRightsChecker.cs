namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Decides whether a user may pin or unpin messages in a peer.
/// Shared by messages.updatePinnedMessage and messages.unpinAllMessages so both methods enforce the
/// same rule: pinning is governed by <c>pin_messages</c> in groups and by <c>edit_messages</c> in
/// broadcast channels.
/// See https://corefork.telegram.org/api/pin and https://corefork.telegram.org/api/rights
/// </summary>
public interface IPinRightsChecker
{
    /// <summary>
    /// Throws the matching RPC error when the user may not pin/unpin in <paramref name="peer"/>.
    /// Private chats are always allowed: both sides may pin in a one-to-one chat.
    /// </summary>
    Task CheckPinRightsAsync(IRequestInput input, Peer peer);
}
