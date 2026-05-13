using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarGifts;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal sealed class GetUniqueStarGiftHandler(
    IMongoDatabase mongoDatabase,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<RequestGetUniqueStarGift, IUniqueStarGift>
{
    protected override async Task<IUniqueStarGift> HandleCoreAsync(IRequestInput input, RequestGetUniqueStarGift obj)
    {
        var doc = await mongoDatabase.GetCollection<UniqueStarGiftDocument>("unique-star-gifts")
            .Find(d => d.Slug == obj.Slug).FirstOrDefaultAsync();

        if (doc == null) RpcErrors.RpcErrors400.StargiftSlugInvalid.ThrowRpcError();

        var users = new TVector<IUser>();
        var chats = new TVector<IChat>();

        // Collect user ids referenced by the gift so the client can resolve them
        // (owner, original sender, original recipient). Without this the client
        // renders these peers as "Deleted Account".
        var userIds = new HashSet<long>();
        if (doc!.OwnerUserId > 0) userIds.Add(doc.OwnerUserId);
        if (!doc.NameHidden && doc.FromUserId > 0) userIds.Add(doc.FromUserId);
        if (doc.OriginalRecipientUserId > 0) userIds.Add(doc.OriginalRecipientUserId);

        if (userIds.Count > 0)
        {
            var userList = await userConverterService.GetUserListAsync(input, userIds.ToList(), layer: input.Layer);
            foreach (var user in userList)
            {
                users.Add(user);
            }
        }

        if (doc.OwnerChannelId > 0)
        {
            var channelList = await chatConverterService.GetChannelListAsync(input, [doc.OwnerChannelId], layer: input.Layer);
            foreach (var channel in channelList)
            {
                chats.Add(channel);
            }
        }

        return new TUniqueStarGift { Gift = UniqueStarGiftHelper.ToTl(doc!), Chats = chats, Users = users };
    }
}
