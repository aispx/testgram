using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Verify a phone number for telegram passport.
/// Possible errors
/// Code Type Description
/// 400 PHONE_CODE_EMPTY phone_code is missing.
/// 400 PHONE_CODE_EXPIRED The phone code you provided has expired.
/// 400 PHONE_NUMBER_INVALID The phone number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.verifyPhone"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class VerifyPhoneHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestVerifyPhone, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestVerifyPhone obj)
    {
        // Validate input
        if (string.IsNullOrWhiteSpace(obj.PhoneNumber))
        {
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        }

        if (string.IsNullOrWhiteSpace(obj.PhoneCode))
        {
            RpcErrors.RpcErrors400.PhoneCodeEmpty.ThrowRpcError();
        }

        if (string.IsNullOrWhiteSpace(obj.PhoneCodeHash))
        {
            RpcErrors.RpcErrors400.PhoneCodeEmpty.ThrowRpcError();
        }

        // Query MongoDB for verification code
        var collection = database.GetCollection<BsonDocument>("phone_verification_codes");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Find code by phone_code_hash
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("PhoneNumber", obj.PhoneNumber),
            Builders<BsonDocument>.Filter.Eq("PhoneCodeHash", obj.PhoneCodeHash),
            Builders<BsonDocument>.Filter.Eq("Code", obj.PhoneCode)
        );

        var doc = await collection.Find(filter).FirstOrDefaultAsync();

        if (doc == null)
        {
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        }

        // Check expiration
        var expireDate = doc["ExpireDate"].AsInt64;
        if (now > expireDate)
        {
            // Clean up expired code
            await collection.DeleteOneAsync(filter);
            RpcErrors.RpcErrors400.PhoneCodeExpired.ThrowRpcError();
        }

        // Delete used code
        await collection.DeleteOneAsync(filter);

        return new TBoolTrue();
    }
}
