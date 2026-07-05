using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Services.Services;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Smoke tests that exercise the shared Phone fixtures so downstream lifecycle tests can rely on them:
/// the in-memory Mongo store's filtering / update / pipeline semantics, the capturing message sender, and
/// the deterministic access-hash fakes.
/// </summary>
public class PhoneTestFixturesTests
{
    [Fact]
    public async Task CallSessions_SupportEqInAndFiltersAndOperatorUpdates()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var collection = database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);

        await collection.InsertOneAsync(new CallSessionDocument
        {
            Id = 5,
            CallId = 5,
            CallerId = 1,
            CalleeId = 2,
            RandomId = 99,
            State = "requested"
        });

        var duplicateFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallerId, 1),
            Builders<CallSessionDocument>.Filter.Eq(s => s.RandomId, 99));
        (await collection.Find(duplicateFilter).AnyAsync()).ShouldBeTrue();

        var busyFilter = Builders<CallSessionDocument>.Filter.And(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CalleeId, 2),
            Builders<CallSessionDocument>.Filter.In(s => s.State, new[] { "received", "accepted", "confirmed" }));
        (await collection.Find(busyFilter).AnyAsync()).ShouldBeFalse();

        await collection.UpdateOneAsync(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, 5),
            Builders<CallSessionDocument>.Update.Set(s => s.State, "received"));

        var session = await collection.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, 5)).FirstOrDefaultAsync();
        session.ShouldNotBeNull();
        session!.State.ShouldBe("received");
        (await collection.Find(busyFilter).AnyAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task GroupCall_PipelineFindOneAndUpdate_AddsParticipantReplacesReJoinAndBumpsVersion()
    {
        var database = PhoneTestFixtures.CreateDatabase(out _);
        var typed = database.GetCollection<GroupCallDocument>(PhoneTestFixtures.GroupCallsCollectionName);
        await typed.InsertOneAsync(new GroupCallDocument
        {
            Id = 10,
            CallId = 10,
            Active = true,
            Version = 1,
            CreatorId = 1,
            PeerId = 1,
            PeerType = (int)PeerType.User
        });

        var bson = database.GetCollection<BsonDocument>(PhoneTestFixtures.GroupCallsCollectionName);

        var afterFirst = await JoinAsync(bson, joiningUserId: 2, source: 123);
        afterFirst.ShouldNotBeNull();
        var firstState = BsonSerializer.Deserialize<GroupCallDocument>(afterFirst!);
        firstState.Version.ShouldBe(2);
        firstState.Participants.Count.ShouldBe(1);
        firstState.Participants[0].UserId.ShouldBe(2);
        firstState.Participants[0].Source.ShouldBe(123);

        // Re-join with a new source must replace (not duplicate) the participant and bump the version again.
        var afterReJoin = await JoinAsync(bson, joiningUserId: 2, source: 456);
        var reJoinState = BsonSerializer.Deserialize<GroupCallDocument>(afterReJoin!);
        reJoinState.Version.ShouldBe(3);
        reJoinState.Participants.Count.ShouldBe(1);
        reJoinState.Participants[0].Source.ShouldBe(456);
    }

    private static Task<BsonDocument?> JoinAsync(IMongoCollection<BsonDocument> collection, long joiningUserId, int source)
    {
        var participant = new GroupCallParticipantDoc
        {
            UserId = joiningUserId,
            PeerId = joiningUserId,
            PeerType = (int)PeerType.User,
            Source = source,
            Date = 100
        };

        var controlledByUser = new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$$participant.UserId", joiningUserId }),
            new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerType", (int)PeerType.User }),
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerId", joiningUserId })
            })
        });

        var set = new BsonDocument
        {
            ["Participants"] = new BsonDocument("$concatArrays", new BsonArray
            {
                new BsonDocument("$filter", new BsonDocument
                {
                    ["input"] = new BsonDocument("$ifNull", new BsonArray { "$Participants", new BsonArray() }),
                    ["as"] = "participant",
                    ["cond"] = new BsonDocument("$not", new BsonArray { controlledByUser })
                }),
                new BsonArray { participant.ToBsonDocument() }
            }),
            ["Version"] = new BsonDocument("$add", new BsonArray { "$Version", 1 })
        };

        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
            new[] { new BsonDocument("$set", set) });

        return collection.FindOneAndUpdateAsync(
            new BsonDocument { ["_id"] = 10L, ["Active"] = true },
            pipeline,
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
    }

    [Fact]
    public void CapturingObjectMessageSender_RecordsTargetAndExclusions()
    {
        var sender = new CapturingObjectMessageSender();
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate> { new TUpdatePhoneCall { PhoneCall = new TPhoneCallDiscarded { Id = 1 } } },
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = 0
        };

        sender.PushMessageToPeerAsync(new Peer(PeerType.User, 2), updates, excludeAuthKeyId: 7, excludeUserId: 1).GetAwaiter().GetResult();

        sender.Pushes.Count.ShouldBe(1);
        var push = sender.Pushes[0];
        push.TargetUserId.ShouldBe(2);
        push.ExcludeUserId.ShouldBe(1);
        push.ExcludeAuthKeyId.ShouldBe(7);
        push.UpdateConstructorNames.ShouldContain(nameof(TUpdatePhoneCall));
        push.Carries<TUpdatePhoneCall>().ShouldBeTrue();
        sender.TargetUserIds.ShouldBe(new[] { 2L });
    }

    [Fact]
    public async Task AccessHashFake_IsDeterministicAndTreatsCallAsGroupCall()
    {
        var helper = new FakeAccessHashHelper2();
        const long userId = 42;
        const long keyId = 1001;
        const long callId = 777;

        var callHash = helper.GenerateAccessHash(userId, keyId, callId, AccessHashType.Call);
        var groupCallHash = helper.GenerateAccessHash(userId, keyId, callId, AccessHashType.GroupCall);
        callHash.ShouldBe(groupCallHash);
        callHash.ShouldNotBe(0);

        (await helper.IsAccessHashValidAsync(userId, keyId, callId, callHash, AccessHashType.Call)).ShouldBeTrue();
        (await helper.IsAccessHashValidAsync(userId, keyId, callId, callHash + 1, AccessHashType.Call)).ShouldBeFalse();

        // A different user (different access-hash key) gets a different hash (per-user authorization).
        var otherHash = helper.GenerateAccessHash(userId + 1, keyId + 1, callId, AccessHashType.Call);
        otherHash.ShouldNotBe(callHash);
    }

    [Fact]
    public async Task UserAccessHashKeyCache_RemembersPerUser()
    {
        var cache = new FakeUserAccessHashKeyCache();
        (await cache.GetAsync(3)).ShouldBeNull();
        await cache.RememberAsync(3, 555);
        (await cache.GetAsync(3)).ShouldBe(555);
    }

    [Fact]
    public void RequestInputBuilder_CreatesDistinctDevicesForSameUser()
    {
        var devices = PhoneTestFixtures.CreateDeviceInputs(userId: 8, deviceCount: 3);

        devices.Count.ShouldBe(3);
        devices.Select(d => d.UserId).Distinct().ShouldBe(new[] { 8L });
        devices.Select(d => d.AccessHashKeyId).Distinct().Count().ShouldBe(1);
        devices.Select(d => d.SessionId).Distinct().Count().ShouldBe(3);
        devices.Select(d => d.AuthKeyId).Distinct().Count().ShouldBe(3);
    }
}
