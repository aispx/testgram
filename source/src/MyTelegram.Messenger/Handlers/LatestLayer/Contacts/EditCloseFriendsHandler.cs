using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;

/// <summary>
/// Edit the <a href="https://corefork.telegram.org/api/privacy">close friends list, see here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.editCloseFriends"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// The request carries the complete list, so it replaces whatever was stored. The list is read back when
/// evaluating the <c>privacyValueAllowCloseFriends</c> story privacy rule.
/// </para>
/// </remarks>
internal sealed class EditCloseFriendsHandler(IMongoDatabase mongoDatabase)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestEditCloseFriends, IBool>
{
    private readonly IMongoCollection<CloseFriendDocument> _closeFriendCollection =
        mongoDatabase.GetCollection<CloseFriendDocument>("close_friends");

    protected override async Task<IBool> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Contacts.RequestEditCloseFriends obj)
    {
        var userIds = obj.Id?
            .Where(id => id > 0 && id != input.UserId)
            .Distinct()
            .ToList() ?? [];

        var doc = new CloseFriendDocument
        {
            Id = CloseFriendDocument.BuildId(input.UserId),
            SelfUserId = input.UserId,
            UserIds = userIds
        };

        await _closeFriendCollection.ReplaceOneAsync(
            p => p.SelfUserId == input.UserId,
            doc,
            new ReplaceOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
