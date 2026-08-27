using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Auth;
using MyTelegram.Services.Services;

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
///
/// <para>The code is queued for the Telegram delivery bot. It used to be POSTed to the bot's own HTTP
/// port under a payload shape the bot did not understand (<c>message</c> where it reads <c>code</c>), so
/// this verification never actually reached anyone.</para>
/// </remarks>
internal sealed class SendVerifyPhoneCodeHandler(
    IMongoDatabase database,
    IBotCodeQueue botCodeQueue,
    ILogger<SendVerifyPhoneCodeHandler> logger) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSendVerifyPhoneCode, MyTelegram.Schema.Auth.ISentCode>
{
    private const int CodeLength = 5;
    private const int TimeoutSeconds = 300;

    protected override async Task<MyTelegram.Schema.Auth.ISentCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSendVerifyPhoneCode obj)
    {
        if (string.IsNullOrWhiteSpace(obj.PhoneNumber))
        {
            RpcErrors.RpcErrors400.PhoneNumberInvalid.ThrowRpcError();
        }

        var code = Random.Shared.Next(10000, 99999).ToString();
        var phoneCodeHash = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expireDate = now + TimeoutSeconds;

        var collection = database.GetCollection<BsonDocument>("phone_verification_codes");

        // Only the newest code of a number is valid.
        await collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("PhoneNumber", obj.PhoneNumber));
        await collection.InsertOneAsync(new BsonDocument
        {
            ["PhoneNumber"] = obj.PhoneNumber,
            ["Code"] = code,
            ["PhoneCodeHash"] = phoneCodeHash,
            ["CreatedAt"] = now,
            ["ExpireDate"] = expireDate,
            ["UserId"] = input.UserId
        });

        if (botCodeQueue.Enabled)
        {
            await botCodeQueue.PublishAsync(obj.PhoneNumber, code, expireDate);
        }
        else
        {
            logger.LogInformation("Phone verification code for {Phone} is {Code} (delivery is disabled)",
                obj.PhoneNumber, code);
        }

        return new TSentCode
        {
            Type = new TSentCodeTypeApp { Length = CodeLength },
            PhoneCodeHash = phoneCodeHash,
            NextType = new TCodeTypeCall(),
            Timeout = TimeoutSeconds
        };
    }
}
