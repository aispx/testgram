using EventFlow.MongoDB.EventStore;
using EventFlow.MongoDB.ReadStores;
using MongoDB.Driver;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

/// <summary>
/// NOTE: nothing invokes this. <c>CreateAllIndexesAsync</c> has exactly one caller
/// (<c>MyTelegramDataSeederBackgroundService</c>), and the seeder resolves
/// <see cref="MyTelegram.ReadModel.MongoDB.MongoDbIndexesCreator"/> — this project is not even reachable
/// from it. Index declarations added here are therefore dead: put them in that creator instead.
/// <para>
/// Kept only because it is referenced by the query server's own composition. The UpdatesReadModel
/// indexes that used to live here have been moved to the creator that actually runs; the collection had
/// been running unindexed as a result.
/// </para>
/// </summary>
public class QueryServerMongoDbIndexesCreator(
    IMongoDatabase database,
    IReadModelDescriptionProvider descriptionProvider,
    IMongoDbEventPersistenceInitializer eventPersistenceInitializer)
    : MongoDbIndexesCreatorBase(database, descriptionProvider, eventPersistenceInitializer), ITransientDependency
{
    protected override async Task CreateAllIndexesCoreAsync()
    {
        await CreateIndexAsync<RpcResultReadModel>(p => p.UserId);
        await CreateIndexAsync<RpcResultReadModel>(p => p.ReqMsgId);

        await CreateIndexAsync<PtsReadModel>(p => p.PeerId);
    }
}