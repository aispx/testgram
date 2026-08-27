namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// State of the TON stake dice game.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiGameInfo"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// <c>emojiGameUnavailable</c> is the correct answer here, not a placeholder: staking a
/// <a href="https://corefork.telegram.org/api/dice">dice</a> needs a TON balance and a commit-reveal
/// round (the client sends a 32-byte <c>client_seed</c> plus the <c>game_hash</c> this method handed out,
/// and the server reveals its own seed in <c>messageMediaDice.game_outcome</c>), none of which exists on
/// this deployment. Clients test availability by exactly this type — Android accepts the feature only when
/// the answer is <c>emojiGameDiceInfo</c>, and tdesktop turns anything else into an empty option list — so
/// answering unavailable hides the staking UI instead of offering a game that cannot be paid for.
/// <c>inputMediaStakeDice</c> is refused with <c>MEDIA_INVALID</c> in <c>MediaHelper</c> for the same
/// reason.
/// </para>
/// </remarks>
internal sealed class GetEmojiGameInfoHandler : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiGameInfo, MyTelegram.Schema.Messages.IEmojiGameInfo>, IObjectHandler
{
    protected override Task<MyTelegram.Schema.Messages.IEmojiGameInfo> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiGameInfo obj)
    {
        return Task.FromResult<MyTelegram.Schema.Messages.IEmojiGameInfo>(new TEmojiGameUnavailable());
    }
}