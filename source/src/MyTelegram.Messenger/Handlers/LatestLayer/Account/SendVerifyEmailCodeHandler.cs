using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Account;
using Microsoft.Extensions.Options;
using System.Net.Mail;
using System.Net;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

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

/// <summary>
/// Send an email verification code.
/// Possible errors
/// Code Type Description
/// 400 EMAIL_INVALID The specified email is invalid.
/// 400 EMAIL_NOT_ALLOWED The specified email cannot be used to complete the operation.
/// 400 EMAIL_NOT_SETUP In order to change the login email with emailVerifyPurposeLoginChange, an existing login email must already be set using emailVerifyPurposeLoginSetup.
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// 400 PHONE_HASH_EXPIRED An invalid or expired <code>phone_code_hash</code> was provided.
/// 400 PHONE_NUMBER_INVALID The phone number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.sendVerifyEmailCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class SendVerifyEmailCodeHandler(
    IMongoDatabase database,
    IOptions<EmailSenderOptions> emailOptions) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendVerifyEmailCode, MyTelegram.Schema.Account.ISentEmailCode>
{
    protected override async Task<MyTelegram.Schema.Account.ISentEmailCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSendVerifyEmailCode obj)
    {
        // Validate email format
        if (string.IsNullOrWhiteSpace(obj.Email) || !obj.Email.Contains("@"))
        {
            RpcErrors.RpcErrors400.EmailInvalid.ThrowRpcError();
        }

        // Generate 6-digit code
        var code = Random.Shared.Next(100000, 999999).ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expireDate = now + 300; // 5 minutes

        // Store code in MongoDB
        var collection = database.GetCollection<BsonDocument>("email_verification_codes");
        var doc = new BsonDocument
        {
            ["Email"] = obj.Email.ToLower(),
            ["Code"] = code,
            ["Purpose"] = obj.Purpose.GetType().Name,
            ["CreatedAt"] = now,
            ["ExpireDate"] = expireDate,
            ["UserId"] = input.UserId
        };

        // Remove old codes for this email
        await collection.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("Email", obj.Email.ToLower())
        );

        await collection.InsertOneAsync(doc);

        // Send email if SMTP is configured
        var options = emailOptions.Value;
        if (options.Enabled && !string.IsNullOrEmpty(options.SmtpEmailOptions.Host))
        {
            try
            {
                await SendEmailAsync(obj.Email, code, options);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL ERROR] Failed to send email: {ex.Message}");
                // Don't fail the request if email sending fails
            }
        }
        else
        {
            // For testing, just log the code
            Console.WriteLine($"[EMAIL VERIFICATION] Email: {obj.Email}, Code: {code}");
        }

        return new TSentEmailCode
        {
            EmailPattern = MaskEmail(obj.Email),
            Length = 6
        };
    }

    private static async Task SendEmailAsync(string toEmail, string code, EmailSenderOptions options)
    {
        using var client = new SmtpClient(options.SmtpEmailOptions.Host, options.SmtpEmailOptions.Port);
        client.EnableSsl = options.SmtpEmailOptions.EnableSsl;
        client.Credentials = new NetworkCredential(
            options.SmtpEmailOptions.UserName,
            options.SmtpEmailOptions.Password
        );

        var message = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = "Testgram Email Verification",
            Body = $"Your verification code is: {code}\n\nThis code will expire in 5 minutes.",
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message);
    }

    private static string MaskEmail(string email)
    {
        var parts = email.Split('@');
        if (parts.Length != 2) return email;

        var local = parts[0];
        var domain = parts[1];

        if (local.Length <= 2)
            return $"{local[0]}***@{domain}";

        return $"{local[0]}***{local[^1]}@{domain}";
    }
}
