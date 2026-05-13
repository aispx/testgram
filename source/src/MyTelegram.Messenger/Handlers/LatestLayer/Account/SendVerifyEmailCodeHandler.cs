using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

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
    IEmailSender emailSender) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendVerifyEmailCode, MyTelegram.Schema.Account.ISentEmailCode>
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

        await emailSender.SendVerificationCodeAsync(obj.Email, "Testgram Email Verification", code);

        return new TSentEmailCode
        {
            EmailPattern = emailSender.MaskEmail(obj.Email),
            Length = 6
        };
    }
}
