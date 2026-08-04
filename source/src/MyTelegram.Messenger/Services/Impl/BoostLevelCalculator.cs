using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Impl;

/// <summary>
/// See https://core.telegram.org/api/boost
/// </summary>
public class BoostLevelCalculator(IMongoDatabase mongoDatabase) : IBoostLevelCalculator, ITransientDependency
{
    public async Task<int> GetTotalBoostsAsync(long channelId)
    {
        var boosts = await mongoDatabase.GetCollection<BsonDocument>("channel_boosts")
            .Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId))
            .ToListAsync();

        return boosts.Sum(b => b.Contains("Multiplier") ? b["Multiplier"].AsInt32 : 1);
    }

    public async Task<int> GetLevelAsync(long channelId)
    {
        return CalculateLevel(await GetTotalBoostsAsync(channelId));
    }

    public int CalculateLevel(int boosts)
    {
        if (boosts < 1) return 0;
        if (boosts < 2) return 1;
        if (boosts < 5) return 2;
        if (boosts < 10) return 3;
        if (boosts < 25) return 4;
        if (boosts < 50) return 5;
        if (boosts < 100) return 6;
        if (boosts < 200) return 7;
        if (boosts < 500) return 8;
        if (boosts < 1000) return 9;
        return 10;
    }

    public int GetBoostsForLevel(int level)
    {
        return level switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            3 => 5,
            4 => 10,
            5 => 25,
            6 => 50,
            7 => 100,
            8 => 200,
            9 => 500,
            10 => 1000,
            _ => 1000
        };
    }
}
