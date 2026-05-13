using System.ComponentModel.DataAnnotations;

namespace MyTelegram.Messenger.Services.Email;

public class EmailSenderOptions
{
    public bool Enabled { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = string.Empty;
    public SmtpEmailOptions SmtpEmailOptions { get; set; } = new();
}

public class SmtpEmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public record EmailSendResult(bool Attempted, bool Sent, string? Error = null);
