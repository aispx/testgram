using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Get recently used <a href="https://corefork.telegram.org/api/emoji-status">emoji statuses</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getRecentEmojiStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRecentEmojiStatusesHandler(IUserAppService userAppService, IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetRecentEmojiStatuses, MyTelegram.Schema.Account.IEmojiStatuses>
{
    protected override async Task<MyTelegram.Schema.Account.IEmojiStatuses> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestGetRecentEmojiStatuses obj)
    {
        if (input.UserId == 0)
        {
            return new TEmojiStatuses
            {
                Hash = 0,
                Statuses = new TVector<IEmojiStatus>()
            };
        }

        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var recentEmojiStatuses = await GetLatestRecentEmojiStatusesAsync(input.UserId);
        if (recentEmojiStatuses.Count == 0 && user!.RecentEmojiStatuses?.Count > 0)
        {
            recentEmojiStatuses = user.RecentEmojiStatuses;
        }

        if (recentEmojiStatuses.Count > 0)
        {
            var hash = ComputeHash(recentEmojiStatuses);
            if (obj.Hash != 0 && obj.Hash == hash)
            {
                return new TEmojiStatusesNotModified();
            }

            return new TEmojiStatuses
            {
                Hash = hash,
                Statuses = new TVector<IEmojiStatus>(recentEmojiStatuses.Select(p => new TEmojiStatus { DocumentId = p }).ToList())
            };
        }

        return new TEmojiStatuses
        {
            Hash = 0,
            Statuses = new TVector<IEmojiStatus>()
        };
    }

    private async Task<List<long>> GetLatestRecentEmojiStatusesAsync(long userId)
    {
        var userDoc = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", userId))
            .Project(Builders<BsonDocument>.Projection.Include("RecentEmojiStatuses"))
            .FirstOrDefaultAsync();

        if (userDoc == null ||
            !userDoc.TryGetValue("RecentEmojiStatuses", out var value) ||
            !value.IsBsonArray)
        {
            return [];
        }

        return value.AsBsonArray
            .Select(GetInt64)
            .Where(x => x != 0)
            .ToList();
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }

    private static long ComputeHash(IEnumerable<long> statuses)
    {
        unchecked
        {
            var hash = 0L;
            foreach (var status in statuses)
            {
                hash = (hash * 20261) + status;
            }

            return hash;
        }
    }
}
