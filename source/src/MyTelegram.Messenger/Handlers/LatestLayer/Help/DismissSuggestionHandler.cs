using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Dismiss a <a href="https://corefork.telegram.org/api/config#suggestions">suggestion, see here for more info »</a>.
/// <para>
/// The dismissal is persisted per user (and per channel, for the pending suggestions carried on
/// <c>channelFull</c>), so the suggestion is not served again after the client restarts. Reads go
/// through <see cref="IDismissedSuggestionAppService"/>.
/// </para>
/// <para><c>See <a href="https://corefork.telegram.org/method/help.dismissSuggestion"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DismissSuggestionHandler(
    IDismissedSuggestionAppService dismissedSuggestionAppService,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<RequestDismissSuggestion, IBool>
{
    /// <summary>Suggestion names are short config identifiers; anything longer is not one.</summary>
    private const int MaxSuggestionLength = 128;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDismissSuggestion obj)
    {
        if (string.IsNullOrWhiteSpace(obj.Suggestion))
        {
            RpcErrors.RpcErrors400.InputTextEmpty.ThrowRpcError();
        }

        if (obj.Suggestion.Length > MaxSuggestionLength)
        {
            RpcErrors.RpcErrors400.InputTextTooLong.ThrowRpcError();
        }

        // Global (app config) suggestions come with inputPeerEmpty; channel suggestions carry the
        // channel, and the access hash is validated by resolving the peer.
        var peer = obj.Peer is TInputPeerEmpty ? null : peerHelper.GetPeer(obj.Peer, input.UserId);

        await dismissedSuggestionAppService.DismissAsync(input.UserId, peer, obj.Suggestion.Trim());

        return new TBoolTrue();
    }
}
