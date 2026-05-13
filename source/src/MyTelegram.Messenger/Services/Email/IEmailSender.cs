namespace MyTelegram.Messenger.Services.Email;

public interface IEmailSender
{
    Task<EmailSendResult> SendVerificationCodeAsync(string toEmail, string subject, string code, string? intro = null);
    string MaskEmail(string email);
}
