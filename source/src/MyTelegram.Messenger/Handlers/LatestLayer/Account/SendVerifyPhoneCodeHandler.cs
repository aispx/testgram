using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Auth;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Send the verification phone code for telegram passport.
/// Possible errors
/// Code Type Description
/// 400 PHONE_NUMBER_INVALID The phone number is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.sendVerifyPhoneCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendVerifyPhoneCodeHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendVerifyPhoneCode, MyTelegram.Schema.Auth.ISentCode>
{
    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSendVerifyPhoneCode obj)
    {
        // Validate phone number format
        if (string.IsNullOrWhiteSpace(obj.PhoneNumber))
        {
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        }

        // Generate 5-digit code
        var code = Random.Shared.Next(10000, 99999).ToString();
        var phoneCodeHash = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expireDate = now + 300; // 5 minutes

        // Store code in MongoDB
        var collection = database.GetCollection<BsonDocument>("phone_verification_codes");
        var doc = new BsonDocument
        {
            ["PhoneNumber"] = obj.PhoneNumber,
            ["Code"] = code,
            ["PhoneCodeHash"] = phoneCodeHash,
            ["CreatedAt"] = now,
            ["ExpireDate"] = expireDate,
            ["UserId"] = input.UserId
        };

        // Remove old codes for this phone
        await collection.DeleteManyAsync(
            Builders<BsonDocument>.Filter.Eq("PhoneNumber", obj.PhoneNumber)
        );

        await collection.InsertOneAsync(doc);

        // For testing, log the code
        Console.WriteLine($"[PHONE VERIFICATION] Phone: {obj.PhoneNumber}, Code: {code}, Hash: {phoneCodeHash}");

        return new TSentCode
        {
            Type = new TSentCodeTypeApp { Length = 5 },
            PhoneCodeHash = phoneCodeHash,
            NextType = new TCodeTypeCall(),
            Timeout = 300
        };
    }
}
