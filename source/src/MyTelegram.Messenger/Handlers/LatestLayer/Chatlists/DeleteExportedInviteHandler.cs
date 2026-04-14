using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Chatlists;
/// <summary>
/// Delete a previously created <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 FILTER_ID_INVALID The specified filter ID is invalid.
/// 400 FILTER_NOT_SUPPORTED The specified filter cannot be used in this context.
/// 400 INVITE_SLUG_EXPIRED The specified chat folder link has expired.
/// 400 INVITE_SLUG_INVALID The specified invitation slug is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/chatlists.deleteExportedInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class DeleteExportedInviteHandler : RpcResultObjectHandler<MyTelegram.Schema.Chatlists.RequestDeleteExportedInvite, IBool>
{
    private readonly IMongoDatabase _database;

    public DeleteExportedInviteHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Chatlists.RequestDeleteExportedInvite obj)
    {
        // 1. Validate chatlist
        if (obj.Chatlist is not TInputChatlistDialogFilter chatlistFilter)
        {
            RpcErrors.RpcErrors400.FilterIdInvalid.ThrowRpcError();
            return null!;
        }

        // 2. Find and revoke invite
        var collection = _database.GetCollection<BsonDocument>("chatlist_invites");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug),
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("FilterId", chatlistFilter.FilterId)
        );

        var update = Builders<BsonDocument>.Update.Set("Revoked", true);
        var result = await collection.UpdateOneAsync(filter, update);

        if (result.MatchedCount == 0)
        {
            RpcErrors.RpcErrors400.InviteSlugInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}