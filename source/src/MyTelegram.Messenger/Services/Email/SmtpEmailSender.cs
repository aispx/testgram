using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace MyTelegram.Messenger.Services.Email;

public class SmtpEmailSender(IOptions<EmailSenderOptions> emailOptions, ILogger<SmtpEmailSender> logger) : IEmailSender, ITransientDependency
{
    public async Task<EmailSendResult> SendVerificationCodeAsync(string toEmail, string subject, string code, string? intro = null)
    {
        var options = emailOptions.Value;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.SmtpEmailOptions.Host))
        {
            logger.LogInformation("[EMAIL DISABLED] To={Email}, Subject={Subject}, Code={Code}", toEmail, subject, code);
            return new EmailSendResult(false, false);
        }

        try
        {
            using var client = new SmtpClient(options.SmtpEmailOptions.Host, options.SmtpEmailOptions.Port)
            {
                EnableSsl = options.SmtpEmailOptions.EnableSsl,
                Credentials = new NetworkCredential(options.SmtpEmailOptions.UserName, options.SmtpEmailOptions.Password)
            };

            using var message = new MailMessage
            {
                From = new MailAddress(options.FromAddress, options.FromDisplayName),
                Subject = subject,
                Body = $"{intro ?? "Your verification code is:"} {code}\n\nThis code will expire in 5 minutes.",
                IsBodyHtml = false
            };
            message.To.Add(toEmail);

            await client.SendMailAsync(message);
            logger.LogInformation("Verification email sent to {Email}", toEmail);
            return new EmailSendResult(true, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send verification email to {Email}", toEmail);
            return new EmailSendResult(true, false, ex.Message);
        }
    }

    public string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2)
        {
            return email;
        }

        var local = parts[0];
        var domain = parts[1];
        if (string.IsNullOrEmpty(local))
        {
            return email;
        }

        if (local.Length <= 2)
        {
            return $"{local[0]}***@{domain}";
        }

        return $"{local[0]}***{local[^1]}@{domain}";
    }
}
