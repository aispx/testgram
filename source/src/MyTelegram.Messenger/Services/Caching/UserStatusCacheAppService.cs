using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Caching;

public class UserStatusMongoModel
{
    public long UserId { get; set; }
    public DateTime LastOnline { get; set; }
}

public class UserStatusCacheAppService(
    IInMemoryRepository<UserStatus, long> inMemoryRepository,
    IMongoDatabase mongoDatabase)
    : IUserStatusCacheAppService, ISingletonDependency
{
    private IMongoCollection<UserStatusMongoModel> Collection =>
        mongoDatabase.GetCollection<UserStatusMongoModel>("user_status");

    public IUserStatus GetUserStatus(long userId)
    {
        var status = inMemoryRepository.Find(userId);
        if (status == null) return new TUserStatusEmpty();
        return GetUserStatus(status.LastUpdateDate, status.Online);
    }

    public void UpdateStatus(long userId, bool online)
    {
        var item = inMemoryRepository.Find(userId);
        if (item == null)
        {
            item = new UserStatus(userId, online);
            inMemoryRepository.Insert(userId, item);
        }
        else
        {
            item.UpdateStatus(online);
        }

        // Persist to MongoDB
        Collection.UpdateOneAsync(
            Builders<UserStatusMongoModel>.Filter.Eq(x => x.UserId, userId),
            Builders<UserStatusMongoModel>.Update.Set(x => x.LastOnline, item.LastUpdateDate),
            new UpdateOptions { IsUpsert = true });
    }

    public async Task LoadFromDatabaseAsync()
    {
        var statuses = await Collection.Find(Builders<UserStatusMongoModel>.Filter.Empty).ToListAsync();
        foreach (var s in statuses)
        {
            var userStatus = new UserStatus(s.UserId, false);
            inMemoryRepository.Insert(s.UserId, userStatus);
            userStatus.SetLastOnline(s.LastOnline);
        }
    }

    // Mirror of the client's presence logic (MessagesController.updateTimerProc):
    // the active account re-sends account.updateStatus(offline=false) roughly every
    // 55s while foreground, and background accounts stop pinging. OnlineWindowSeconds (90s)
    // covers the ~55s client refresh plus network margin.
    private const int OnlineWindowSeconds = 90;

    private static IUserStatus GetUserStatus(DateTime lastUpdateUtcTime, bool isOnline)
    {
        var now = DateTime.UtcNow;
        var timespan = (now - lastUpdateUtcTime).TotalSeconds;
        const int day = 60 * 60 * 24;
        var expire = lastUpdateUtcTime.AddSeconds(OnlineWindowSeconds).ToTimestamp();
        var wasOnline = Math.Min(lastUpdateUtcTime.ToTimestamp(), now.ToTimestamp() - 1);

        IUserStatus status;
        if (isOnline)
            status = timespan switch
            {
                < OnlineWindowSeconds => new TUserStatusOnline { Expires = expire },
                < 60 * 7 => new TUserStatusOffline { WasOnline = wasOnline },
                < day * 1 => new TUserStatusRecently(),
                < day * 14 => new TUserStatusLastWeek(),
                < day * 30 => new TUserStatusLastMonth(),
                _ => new TUserStatusEmpty()
            };
        else
            status = new TUserStatusOffline { WasOnline = wasOnline };

        return status;
    }
}
