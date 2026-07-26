using MyTelegram.Messenger.Services.SecretChat;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Report a secret chat for spam
/// Possible errors
/// Code Type Description
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.reportEncryptedSpam"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReportEncryptedSpamHandler(ISecretChatAppService secretChatAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReportEncryptedSpam, IBool>
{
    protected override Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReportEncryptedSpam obj)
    {
        return secretChatAppService.ReportEncryptedSpamAsync(input, obj.Peer);
    }
}