using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Obtain user info from a <a href="https://corefork.telegram.org/api/links#temporary-profile-links">temporary profile link</a>.
/// Possible errors
/// Code Type Description
/// 400 IMPORT_TOKEN_INVALID The specified token is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.importContactToken"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ImportContactTokenHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestImportContactToken, MyTelegram.Schema.IUser>
{
    protected override async Task<MyTelegram.Schema.IUser> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestImportContactToken obj)
    {
        if (!IsValidToken(obj.Token))
        {
            RpcErrors.RpcErrors400.ImportTokenInvalid.ThrowRpcError();
        }

        var doc = await mongoDatabase.GetCollection<BsonDocument>("contact_tokens")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", obj.Token))
            .FirstOrDefaultAsync();
        if (doc == null || doc.GetValue("Expires", 0).ToInt32() < CurrentDate)
        {
            RpcErrors.RpcErrors400.ImportTokenInvalid.ThrowRpcError();
        }

        return await userConverterService.GetUserAsync(
            input,
            doc.GetValue("UserId", 0L).ToInt64(),
            skipSetContactProperties: false,
            skipPrivacy: false,
            input.Layer);
    }

    private static bool IsValidToken(string token)
    {
        return token.Length is > 0 and <= 256 &&
               token.All(c => (c >= 'A' && c <= 'Z') ||
                              (c >= 'a' && c <= 'z') ||
                              (c >= '0' && c <= '9') ||
                              c is '-' or '_');
    }
}
