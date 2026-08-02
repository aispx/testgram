using EventFlow.MongoDB.EventStore;
using EventFlow.MongoDB.ReadStores;
using MongoDB.Driver;

namespace MyTelegram.Messenger.QueryServer.BackgroundServices;

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

        await CreateIndexAsync<UpdatesReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.ChannelId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.Pts);
        // Serves the secret-chat handshake replay (GetUpdatesByGlobalSeqNoQuery), which every
        // updates.getDifference issues. Without it that query degrades to an OwnerPeerId-index scan over a
        // collection that is append-only and never pruned.
        await CreateIndexAsync<UpdatesReadModel>(p => p.GlobalSeqNo);

        await CreateIndexAsync<PtsReadModel>(p => p.PeerId);
    }
}