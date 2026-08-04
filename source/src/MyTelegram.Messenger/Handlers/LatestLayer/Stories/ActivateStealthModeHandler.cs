using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Stories;
using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;

/// <summary>
/// Activates <a href="https://corefork.telegram.org/api/stories#stealth-mode">stories stealth mode</a>,
/// see here » for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/stories.activateStealthMode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// <para>
/// <c>future</c> hides views for the next <c>stories_stealth_future_period</c> seconds;
/// <c>past</c> retroactively erases views recorded in the last <c>stories_stealth_past_period</c>
/// seconds. Re-activation is rate-limited by <c>stories_stealth_cooldown_period</c>.
/// </para>
/// </remarks>
internal sealed class ActivateStealthModeHandler(
    IMongoDatabase mongoDatabase,
    IStoryConfigProvider storyConfigProvider)
    : RpcResultObjectHandler<RequestActivateStealthMode, IUpdates>
{
    private readonly IMongoCollection<StoryStealthDocument> _stealthCollection =
        mongoDatabase.GetCollection<StoryStealthDocument>("story_stealth_modes");
    private readonly IMongoCollection<StoryDocument> _storyCollection =
        mongoDatabase.GetCollection<StoryDocument>("stories");
    private readonly IMongoCollection<BsonDocument> _storyViewsCollection =
        mongoDatabase.GetCollection<BsonDocument>("story_views");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestActivateStealthMode obj)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var existing = await _stealthCollection.Find(p => p.UserId == input.UserId).FirstOrDefaultAsync();
        if (existing != null && existing.IsOnCooldown(now))
        {
            RpcErrors.RpcErrors420.FloodWaitX.ThrowRpcError(existing.CooldownUntilDate!.Value - (int)now);
        }

        if (obj.Past)
        {
            await ErasePastViewsAsync(input.UserId, now);
        }

        var document = new StoryStealthDocument
        {
            UserId = input.UserId,
            ActiveUntilDate = obj.Future
                ? (int)now + storyConfigProvider.GetStealthFuturePeriod()
                : existing?.ActiveUntilDate,
            CooldownUntilDate = (int)now + storyConfigProvider.GetStealthCooldownPeriod()
        };

        await _stealthCollection.ReplaceOneAsync(
            p => p.UserId == input.UserId,
            document,
            new ReplaceOptions { IsUpsert = true });

        return new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateStoriesStealthMode
                {
                    StealthMode = new TStoriesStealthMode
                    {
                        ActiveUntilDate = document.ActiveUntilDate,
                        CooldownUntilDate = document.CooldownUntilDate
                    }
                }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = (int)now
        };
    }

    /// <summary>
    /// Removes this user's recent view records and decrements the affected stories' counters, so the
    /// poster no longer sees that the user watched.
    /// </summary>
    private async Task ErasePastViewsAsync(long userId, long now)
    {
        var since = now - storyConfigProvider.GetStealthPastPeriod();

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("viewerUserId", userId),
            Builders<BsonDocument>.Filter.Gte("date", since)
        );

        var views = await _storyViewsCollection.Find(filter).ToListAsync();
        if (views.Count == 0)
        {
            return;
        }

        await _storyViewsCollection.DeleteManyAsync(filter);

        foreach (var view in views)
        {
            if (!view.Contains("storyId") || !view.Contains("ownerPeerId") || !view.Contains("ownerPeerType"))
            {
                continue;
            }

            var storyId = view["storyId"].AsInt32;
            var ownerPeerId = view["ownerPeerId"].AsInt64;
            var ownerPeerType = view["ownerPeerType"].AsInt32;

            await _storyCollection.UpdateOneAsync(
                Builders<StoryDocument>.Filter.And(
                    Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerId, ownerPeerId),
                    Builders<StoryDocument>.Filter.Eq(s => s.OwnerPeerType, ownerPeerType),
                    Builders<StoryDocument>.Filter.Eq(s => s.StoryId, storyId),
                    // Guard against driving the counter negative.
                    Builders<StoryDocument>.Filter.Gt(s => s.ViewsCount, 0)),
                Builders<StoryDocument>.Update.Inc(s => s.ViewsCount, -1));
        }
    }
}
