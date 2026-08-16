using MyTelegram.Messenger.Services.SecretChat;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Marks message history within a secret chat as read.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 MAX_DATE_INVALID The specified maximum date is invalid.
/// 400 MSG_WAIT_FAILED A waiting call returned an error.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readEncryptedHistory"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadEncryptedHistoryHandler(ISecretChatAppService secretChatAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadEncryptedHistory, IBool>
{
    /// <summary>Clock-skew allowance for a read mark slightly ahead of the server's current time.</summary>
    private const int MaxClockSkewSeconds = 60;

    protected override Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadEncryptedHistory obj)
    {
        // max_date is relayed to the peer as updateEncryptedMessagesRead, which marks everything up to
        // that date as read. Without a bound, a participant could send int.MaxValue and mark the whole
        // outbox — including messages not yet actually read, and any sent later with a lower date — as
        // read. A read mark can only reference messages that already exist, so it must not be negative
        // or meaningfully in the future.
        if (obj.MaxDate < 0 || obj.MaxDate > CurrentDate + MaxClockSkewSeconds)
        {
            RpcErrors.RpcErrors400.MaxDateInvalid.ThrowRpcError();
        }

        return secretChatAppService.ReadEncryptedHistoryAsync(input, obj.Peer, obj.MaxDate);
    }
}