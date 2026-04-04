using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Verify an email address.
/// Possible errors
/// Code Type Description
/// 400 CODE_INVALID Code invalid (i.e. from email).
/// 400 EMAIL_VERIFY_EXPIRED The verification email has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.verifyEmail"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class VerifyEmailHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestVerifyEmail, MyTelegram.Schema.Account.IEmailVerified>
{
    protected override async Task<MyTelegram.Schema.Account.IEmailVerified> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestVerifyEmail obj)
    {
        string? code = null;
        string? email = null;

        // Extract code from verification
        if (obj.Verification is MyTelegram.Schema.TEmailVerificationCode codeVerification)
        {
            code = codeVerification.Code;
        }
        else if (obj.Verification is MyTelegram.Schema.TEmailVerificationGoogle googleVerification)
        {
            // Google token verification not implemented yet
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }
        else if (obj.Verification is MyTelegram.Schema.TEmailVerificationApple appleVerification)
        {
            // Apple token verification not implemented yet
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }

        // Query MongoDB for verification code
        var collection = database.GetCollection<BsonDocument>("email_verification_codes");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Find code for this user
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("Code", code)
        );

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        if (doc == null)
        {
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }

        // Check expiration
        var expireDate = doc["ExpireDate"].AsInt64;
        if (now > expireDate)
        {
            // Clean up expired code
            await collection.DeleteOneAsync(filter);
            RpcErrors.RpcErrors400.EmailVerifyExpired.ThrowRpcError();
        }

        email = doc["Email"].AsString;

        // Verify purpose matches
        var storedPurpose = doc["Purpose"].AsString;
        var requestedPurpose = obj.Purpose.GetType().Name;

        if (storedPurpose != requestedPurpose)
        {
            RpcErrors.RpcErrors400.CodeInvalid.ThrowRpcError();
        }

        // Delete used code
        await collection.DeleteOneAsync(filter);

        // Update user's email in database based on purpose
        if (obj.Purpose is MyTelegram.Schema.TEmailVerifyPurposeLoginSetup or MyTelegram.Schema.TEmailVerifyPurposeLoginChange)
        {
            var userCollection = database.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var userFilter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
            var userUpdate = Builders<BsonDocument>.Update
                .Set("Email", email)
                .Set("EmailVerified", true)
                .Set("EmailVerifiedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            await userCollection.UpdateOneAsync(userFilter, userUpdate);
        }
        else if (obj.Purpose is MyTelegram.Schema.TEmailVerifyPurposePassport)
        {
            // For passport, store in separate collection
            var passportCollection = database.GetCollection<BsonDocument>("passport_emails");
            var passportDoc = new BsonDocument
            {
                ["_id"] = $"passport-email-{input.UserId}",
                ["UserId"] = input.UserId,
                ["Email"] = email,
                ["VerifiedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            await passportCollection.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", passportDoc["_id"]),
                passportDoc,
                new ReplaceOptions { IsUpsert = true }
            );
        }

        // Return appropriate response based on purpose
        return new TEmailVerified
        {
            Email = email
        };
    }
}
