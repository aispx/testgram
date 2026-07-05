using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// A minimal in-memory MongoDB backing store. Documents are kept as canonical <see cref="BsonDocument"/>s
/// keyed by collection name, so a typed collection (e.g. <c>IMongoCollection&lt;GroupCallDocument&gt;</c>)
/// and a <c>IMongoCollection&lt;BsonDocument&gt;</c> over the same name share the same data - exactly as the
/// call handlers use them (see JoinGroupCallHandler).
///
/// Supported operations mirror what the Phone handlers exercise: filtered find (with sort / skip / limit),
/// insert, replace, update ($set / $inc / $unset / $push / $pull / $addToSet), delete, count, and
/// find-one-and-update with both operator updates and aggregation-pipeline updates.
/// </summary>
public sealed class InMemoryMongoStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<BsonDocument>> _collections = new();

    public InMemoryMongoStore()
    {
        Database = new InMemoryMongoDatabase(this);
    }

    /// <summary>The in-memory <see cref="IMongoDatabase"/>.</summary>
    public IMongoDatabase Database { get; }

    internal object SyncRoot => _gate;

    internal List<BsonDocument> GetList(string name)
    {
        lock (_gate)
        {
            if (!_collections.TryGetValue(name, out var list))
            {
                list = new List<BsonDocument>();
                _collections[name] = list;
            }

            return list;
        }
    }

    /// <summary>Returns a defensive copy of the stored documents for a collection (for assertions).</summary>
    public IReadOnlyList<BsonDocument> Documents(string name)
    {
        lock (_gate)
        {
            return GetList(name).Select(d => (BsonDocument)d.DeepClone()).ToList();
        }
    }

    /// <summary>Number of documents currently stored in the named collection.</summary>
    public int Count(string name)
    {
        lock (_gate)
        {
            return GetList(name).Count;
        }
    }

    /// <summary>Directly seeds a strongly-typed document into a collection (bypassing the driver surface).</summary>
    public void Seed<TDocument>(string name, TDocument document) where TDocument : notnull
    {
        lock (_gate)
        {
            GetList(name).Add(BsonQueryEngine.ToBsonDocument(document));
        }
    }
}

/// <summary>
/// A minimal <see cref="IMongoDatabase"/> that hands out <see cref="InMemoryMongoCollection{TDocument}"/>
/// instances backed by a shared <see cref="InMemoryMongoStore"/>. Only <c>GetCollection</c> is supported;
/// administrative members throw.
/// </summary>
public sealed class InMemoryMongoDatabase : IMongoDatabase
{
    private readonly InMemoryMongoStore _store;

    public InMemoryMongoDatabase(InMemoryMongoStore store)
    {
        _store = store;
        DatabaseNamespace = new DatabaseNamespace("test");
    }

    public IMongoClient Client => throw NotUsed();
    public DatabaseNamespace DatabaseNamespace { get; }
    public MongoDatabaseSettings Settings { get; } = new();

    public IMongoCollection<TDocument> GetCollection<TDocument>(string name, MongoCollectionSettings? settings = null)
        => new InMemoryMongoCollection<TDocument>(_store, name, this);

    public IMongoDatabase WithReadConcern(ReadConcern readConcern) => this;
    public IMongoDatabase WithReadPreference(ReadPreference readPreference) => this;
    public IMongoDatabase WithWriteConcern(WriteConcern writeConcern) => this;

    public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void AggregateToCollection<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<NoPipelineInput, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public void CreateCollection(string name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) => _store.GetList(name);
    public void CreateCollection(IClientSessionHandle session, string name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) => _store.GetList(name);
    public Task CreateCollectionAsync(string name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) { _store.GetList(name); return Task.CompletedTask; }
    public Task CreateCollectionAsync(IClientSessionHandle session, string name, CreateCollectionOptions? options = null, CancellationToken cancellationToken = default) { _store.GetList(name); return Task.CompletedTask; }

    public void CreateView<TDocument, TResult>(string viewName, string viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void CreateView<TDocument, TResult>(IClientSessionHandle session, string viewName, string viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task CreateViewAsync<TDocument, TResult>(string viewName, string viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task CreateViewAsync<TDocument, TResult>(IClientSessionHandle session, string viewName, string viewOn, PipelineDefinition<TDocument, TResult> pipeline, CreateViewOptions<TDocument>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public void DropCollection(string name, CancellationToken cancellationToken = default) => throw NotUsed();
    public void DropCollection(string name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw NotUsed();
    public void DropCollection(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw NotUsed();
    public void DropCollection(IClientSessionHandle session, string name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task DropCollectionAsync(string name, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task DropCollectionAsync(string name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task DropCollectionAsync(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task DropCollectionAsync(IClientSessionHandle session, string name, DropCollectionOptions options, CancellationToken cancellationToken = default) => throw NotUsed();

    public IAsyncCursor<string> ListCollectionNames(ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<string> ListCollectionNames(IClientSessionHandle session, ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<string>> ListCollectionNamesAsync(ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<string>> ListCollectionNamesAsync(IClientSessionHandle session, ListCollectionNamesOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public IAsyncCursor<BsonDocument> ListCollections(ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<BsonDocument> ListCollections(IClientSessionHandle session, ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<BsonDocument>> ListCollectionsAsync(IClientSessionHandle session, ListCollectionsOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public void RenameCollection(string oldName, string newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void RenameCollection(IClientSessionHandle session, string oldName, string newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task RenameCollectionAsync(string oldName, string newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task RenameCollectionAsync(IClientSessionHandle session, string oldName, string newName, RenameCollectionOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public TResult RunCommand<TResult>(Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public TResult RunCommand<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<TResult> RunCommandAsync<TResult>(Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<TResult> RunCommandAsync<TResult>(IClientSessionHandle session, Command<TResult> command, ReadPreference? readPreference = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    private static NotSupportedException NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"{nameof(InMemoryMongoDatabase)}.{member} is not supported by the in-memory test store.");
}

/// <summary>An <see cref="IAsyncCursor{TDocument}"/> that yields a single in-memory batch.</summary>
public sealed class InMemoryAsyncCursor<TDocument> : IAsyncCursor<TDocument>
{
    private readonly IReadOnlyList<TDocument> _items;
    private bool _moved;

    public InMemoryAsyncCursor(IReadOnlyList<TDocument> items)
    {
        _items = items;
    }

    public IEnumerable<TDocument> Current { get; private set; } = Array.Empty<TDocument>();

    public bool MoveNext(CancellationToken cancellationToken = default)
    {
        if (_moved)
        {
            Current = Array.Empty<TDocument>();
            return false;
        }

        _moved = true;
        Current = _items;
        return true;
    }

    public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(MoveNext(cancellationToken));
    }

    public void Dispose()
    {
    }
}

/// <summary>In-memory <see cref="IMongoCollection{TDocument}"/> backed by <see cref="InMemoryMongoStore"/>.</summary>
public sealed class InMemoryMongoCollection<TDocument> : IMongoCollection<TDocument>
{
    private readonly InMemoryMongoStore _store;
    private readonly string _name;

    public InMemoryMongoCollection(InMemoryMongoStore store, string name, IMongoDatabase database)
    {
        _store = store;
        _name = name;
        Database = database;
        CollectionNamespace = new CollectionNamespace("test", name);
    }

    public CollectionNamespace CollectionNamespace { get; }
    public IMongoDatabase Database { get; }
    public IBsonSerializer<TDocument> DocumentSerializer => BsonSerializer.SerializerRegistry.GetSerializer<TDocument>();
    public IMongoIndexManager<TDocument> Indexes => throw NotUsed();
    public IMongoSearchIndexManager SearchIndexes => throw NotUsed();
    public MongoCollectionSettings Settings { get; } = new();

    private List<BsonDocument> Docs => _store.GetList(_name);
    private object Gate => _store.SyncRoot;

    // ---- core query / mutation logic -------------------------------------------------------------

    private List<BsonDocument> Query<TProjection>(FilterDefinition<TDocument> filter, FindOptions<TDocument, TProjection>? options)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        lock (Gate)
        {
            IEnumerable<BsonDocument> query = Docs.Where(d => BsonQueryEngine.Matches(d, filterDoc));

            if (options?.Sort != null)
            {
                var sortDoc = BsonQueryEngine.RenderSort(options.Sort);
                query = BsonQueryEngine.ApplySort(query, sortDoc);
            }

            if (options?.Skip is { } skip)
            {
                query = query.Skip(skip);
            }

            if (options?.Limit is { } limit)
            {
                query = query.Take(limit);
            }

            return query.Select(d => (BsonDocument)d.DeepClone()).ToList();
        }
    }

    private int FirstMatchIndex(BsonDocument filterDoc, BsonDocument sortDoc)
    {
        var ordered = BsonQueryEngine.ApplySort(
                Docs.Select((doc, index) => (doc, index)).Where(x => BsonQueryEngine.Matches(x.doc, filterDoc)),
                sortDoc,
                x => x.doc)
            .ToList();
        return ordered.Count == 0 ? -1 : ordered[0].index;
    }

    private long UpdateCore(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, bool many)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        var rendered = BsonQueryEngine.RenderUpdate(update);
        lock (Gate)
        {
            long modified = 0;
            for (var i = 0; i < Docs.Count; i++)
            {
                if (!BsonQueryEngine.Matches(Docs[i], filterDoc))
                {
                    continue;
                }

                var clone = (BsonDocument)Docs[i].DeepClone();
                BsonQueryEngine.ApplyUpdate(clone, rendered);
                Docs[i] = clone;
                modified++;
                if (!many)
                {
                    break;
                }
            }

            return modified;
        }
    }

    private long ReplaceCore(FilterDefinition<TDocument> filter, object replacement)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        var replacementDoc = BsonQueryEngine.ToBsonDocument(replacement);
        lock (Gate)
        {
            for (var i = 0; i < Docs.Count; i++)
            {
                if (!BsonQueryEngine.Matches(Docs[i], filterDoc))
                {
                    continue;
                }

                if (!replacementDoc.Contains("_id") && Docs[i].TryGetValue("_id", out var id))
                {
                    replacementDoc = (BsonDocument)replacementDoc.DeepClone();
                    replacementDoc.InsertAt(0, new BsonElement("_id", id));
                }

                Docs[i] = (BsonDocument)replacementDoc.DeepClone();
                return 1;
            }

            return 0;
        }
    }

    private long DeleteCore(FilterDefinition<TDocument> filter, bool many)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        lock (Gate)
        {
            long deleted = 0;
            for (var i = Docs.Count - 1; i >= 0; i--)
            {
                if (!BsonQueryEngine.Matches(Docs[i], filterDoc))
                {
                    continue;
                }

                Docs.RemoveAt(i);
                deleted++;
                if (!many)
                {
                    break;
                }
            }

            return deleted;
        }
    }

    private long CountCore(FilterDefinition<TDocument> filter)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        lock (Gate)
        {
            return Docs.Count(d => BsonQueryEngine.Matches(d, filterDoc));
        }
    }

    private TProjection FindOneAndModify<TProjection>(
        FilterDefinition<TDocument> filter,
        BsonValue? renderedUpdate,
        SortDefinition<TDocument>? sort,
        ReturnDocument returnDocument,
        bool delete)
    {
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        var sortDoc = sort != null ? BsonQueryEngine.RenderSort(sort) : new BsonDocument();
        lock (Gate)
        {
            var index = FirstMatchIndex(filterDoc, sortDoc);
            if (index < 0)
            {
                return default!;
            }

            var before = (BsonDocument)Docs[index].DeepClone();
            if (delete)
            {
                Docs.RemoveAt(index);
                return BsonQueryEngine.Deserialize<TProjection>(before);
            }

            var after = (BsonDocument)Docs[index].DeepClone();
            BsonQueryEngine.ApplyUpdate(after, renderedUpdate!);
            Docs[index] = after;
            var result = returnDocument == ReturnDocument.After ? after : before;
            return BsonQueryEngine.Deserialize<TProjection>(result);
        }
    }

    // ---- find ------------------------------------------------------------------------------------

    public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
        FilterDefinition<TDocument> filter,
        FindOptions<TDocument, TProjection>? options = null,
        CancellationToken cancellationToken = default)
    {
        var projected = Query(filter, options).Select(BsonQueryEngine.Deserialize<TProjection>).ToList();
        return Task.FromResult<IAsyncCursor<TProjection>>(new InMemoryAsyncCursor<TProjection>(projected));
    }

    public Task<IAsyncCursor<TProjection>> FindAsync<TProjection>(
        IClientSessionHandle session,
        FilterDefinition<TDocument> filter,
        FindOptions<TDocument, TProjection>? options = null,
        CancellationToken cancellationToken = default)
        => FindAsync(filter, options, cancellationToken);

    public IAsyncCursor<TProjection> FindSync<TProjection>(
        FilterDefinition<TDocument> filter,
        FindOptions<TDocument, TProjection>? options = null,
        CancellationToken cancellationToken = default)
    {
        var projected = Query(filter, options).Select(BsonQueryEngine.Deserialize<TProjection>).ToList();
        return new InMemoryAsyncCursor<TProjection>(projected);
    }

    public IAsyncCursor<TProjection> FindSync<TProjection>(
        IClientSessionHandle session,
        FilterDefinition<TDocument> filter,
        FindOptions<TDocument, TProjection>? options = null,
        CancellationToken cancellationToken = default)
        => FindSync(filter, options, cancellationToken);

    // ---- insert ----------------------------------------------------------------------------------

    public void InsertOne(TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            Docs.Add(BsonQueryEngine.ToBsonDocument(document!));
        }
    }

    public void InsertOne(IClientSessionHandle session, TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
        => InsertOne(document, options, cancellationToken);

    public Task InsertOneAsync(TDocument document, CancellationToken cancellationToken)
    {
        InsertOne(document);
        return Task.CompletedTask;
    }

    public Task InsertOneAsync(TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
    {
        InsertOne(document, options, cancellationToken);
        return Task.CompletedTask;
    }

    public Task InsertOneAsync(IClientSessionHandle session, TDocument document, InsertOneOptions? options = null, CancellationToken cancellationToken = default)
    {
        InsertOne(document, options, cancellationToken);
        return Task.CompletedTask;
    }

    public void InsertMany(IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            foreach (var document in documents)
            {
                Docs.Add(BsonQueryEngine.ToBsonDocument(document!));
            }
        }
    }

    public void InsertMany(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
        => InsertMany(documents, options, cancellationToken);

    public Task InsertManyAsync(IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
    {
        InsertMany(documents, options, cancellationToken);
        return Task.CompletedTask;
    }

    public Task InsertManyAsync(IClientSessionHandle session, IEnumerable<TDocument> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
    {
        InsertMany(documents, options, cancellationToken);
        return Task.CompletedTask;
    }

    // ---- replace ---------------------------------------------------------------------------------

    public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        var matched = ReplaceCore(filter, replacement!);
        return new ReplaceOneResult.Acknowledged(matched, matched, null);
    }

    public ReplaceOneResult ReplaceOne(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => ReplaceOne(filter, replacement, new ReplaceOptions { IsUpsert = options?.IsUpsert ?? false }, cancellationToken);

    public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => ReplaceOne(filter, replacement, options, cancellationToken);

    public ReplaceOneResult ReplaceOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => ReplaceOne(filter, replacement, options, cancellationToken);

    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(ReplaceOne(filter, replacement, options, cancellationToken));

    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(ReplaceOne(filter, replacement, options, cancellationToken));

    public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(ReplaceOne(filter, replacement, options, cancellationToken));

    public Task<ReplaceOneResult> ReplaceOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, UpdateOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(ReplaceOne(filter, replacement, options, cancellationToken));

    // ---- update ----------------------------------------------------------------------------------

    public UpdateResult UpdateOne(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
    {
        var modified = UpdateCore(filter, update, many: false);
        return new UpdateResult.Acknowledged(modified, modified, null);
    }

    public UpdateResult UpdateOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => UpdateOne(filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateOne(filter, update, options, cancellationToken));

    public Task<UpdateResult> UpdateOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateOne(filter, update, options, cancellationToken));

    public UpdateResult UpdateMany(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
    {
        var modified = UpdateCore(filter, update, many: true);
        return new UpdateResult.Acknowledged(modified, modified, null);
    }

    public UpdateResult UpdateMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => UpdateMany(filter, update, options, cancellationToken);

    public Task<UpdateResult> UpdateManyAsync(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateMany(filter, update, options, cancellationToken));

    public Task<UpdateResult> UpdateManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, UpdateOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateMany(filter, update, options, cancellationToken));

    // ---- delete ----------------------------------------------------------------------------------

    public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: false));

    public DeleteResult DeleteOne(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: false));

    public DeleteResult DeleteOne(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: false));

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: false)));

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: false)));

    public Task<DeleteResult> DeleteOneAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: false)));

    public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: true));

    public DeleteResult DeleteMany(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: true));

    public DeleteResult DeleteMany(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => new DeleteResult.Acknowledged(DeleteCore(filter, many: true));

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: true)));

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<TDocument> filter, DeleteOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: true)));

    public Task<DeleteResult> DeleteManyAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult<DeleteResult>(new DeleteResult.Acknowledged(DeleteCore(filter, many: true)));

    // ---- count -----------------------------------------------------------------------------------

    public long CountDocuments(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => CountCore(filter);

    public long CountDocuments(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => CountCore(filter);

    public Task<long> CountDocumentsAsync(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CountCore(filter));

    public Task<long> CountDocumentsAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CountCore(filter));

#pragma warning disable CS0618
    public long Count(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => CountCore(filter);

    public long Count(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => CountCore(filter);

    public Task<long> CountAsync(FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CountCore(filter));

    public Task<long> CountAsync(IClientSessionHandle session, FilterDefinition<TDocument> filter, CountOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CountCore(filter));
#pragma warning restore CS0618

    public long EstimatedDocumentCount(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            return Docs.Count;
        }
    }

    public Task<long> EstimatedDocumentCountAsync(EstimatedDocumentCountOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(EstimatedDocumentCount(options, cancellationToken));

    // ---- find-one-and-* --------------------------------------------------------------------------

    public TProjection FindOneAndUpdate<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => FindOneAndModify<TProjection>(filter, BsonQueryEngine.RenderUpdate(update), options?.Sort, options?.ReturnDocument ?? ReturnDocument.Before, delete: false);

    public TProjection FindOneAndUpdate<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => FindOneAndUpdate(filter, update, options, cancellationToken);

    public Task<TProjection> FindOneAndUpdateAsync<TProjection>(FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndUpdate(filter, update, options, cancellationToken));

    public Task<TProjection> FindOneAndUpdateAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, UpdateDefinition<TDocument> update, FindOneAndUpdateOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndUpdate(filter, update, options, cancellationToken));

    public TProjection FindOneAndReplace<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
    {
        var returnDocument = options?.ReturnDocument ?? ReturnDocument.Before;
        var filterDoc = BsonQueryEngine.RenderFilter(filter);
        var sortDoc = options?.Sort != null ? BsonQueryEngine.RenderSort(options.Sort) : new BsonDocument();
        var replacementDoc = BsonQueryEngine.ToBsonDocument(replacement!);
        lock (Gate)
        {
            var index = FirstMatchIndex(filterDoc, sortDoc);
            if (index < 0)
            {
                return default!;
            }

            var before = (BsonDocument)Docs[index].DeepClone();
            if (!replacementDoc.Contains("_id") && before.TryGetValue("_id", out var id))
            {
                replacementDoc = (BsonDocument)replacementDoc.DeepClone();
                replacementDoc.InsertAt(0, new BsonElement("_id", id));
            }

            Docs[index] = (BsonDocument)replacementDoc.DeepClone();
            var result = returnDocument == ReturnDocument.After ? Docs[index] : before;
            return BsonQueryEngine.Deserialize<TProjection>(result);
        }
    }

    public TProjection FindOneAndReplace<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => FindOneAndReplace(filter, replacement, options, cancellationToken);

    public Task<TProjection> FindOneAndReplaceAsync<TProjection>(FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndReplace(filter, replacement, options, cancellationToken));

    public Task<TProjection> FindOneAndReplaceAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, TDocument replacement, FindOneAndReplaceOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndReplace(filter, replacement, options, cancellationToken));

    public TProjection FindOneAndDelete<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => FindOneAndModify<TProjection>(filter, null, options?.Sort, ReturnDocument.Before, delete: true);

    public TProjection FindOneAndDelete<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => FindOneAndDelete(filter, options, cancellationToken);

    public Task<TProjection> FindOneAndDeleteAsync<TProjection>(FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndDelete(filter, options, cancellationToken));

    public Task<TProjection> FindOneAndDeleteAsync<TProjection>(IClientSessionHandle session, FilterDefinition<TDocument> filter, FindOneAndDeleteOptions<TDocument, TProjection>? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FindOneAndDelete(filter, options, cancellationToken));

    // ---- with-* (return self) --------------------------------------------------------------------

    public IMongoCollection<TDocument> WithReadConcern(ReadConcern readConcern) => this;
    public IMongoCollection<TDocument> WithReadPreference(ReadPreference readPreference) => this;
    public IMongoCollection<TDocument> WithWriteConcern(WriteConcern writeConcern) => this;

    // ---- unsupported members ---------------------------------------------------------------------

    public IAsyncCursor<TResult> Aggregate<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TResult> Aggregate<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> AggregateAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void AggregateToCollection<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public void AggregateToCollection<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task AggregateToCollectionAsync<TResult>(PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task AggregateToCollectionAsync<TResult>(IClientSessionHandle session, PipelineDefinition<TDocument, TResult> pipeline, AggregateOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public BulkWriteResult<TDocument> BulkWrite(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public BulkWriteResult<TDocument> BulkWrite(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<BulkWriteResult<TDocument>> BulkWriteAsync(IClientSessionHandle session, IEnumerable<WriteModel<TDocument>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public IAsyncCursor<TField> Distinct<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TField> Distinct<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TField>> DistinctAsync<TField>(FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TField>> DistinctAsync<TField>(IClientSessionHandle session, FieldDefinition<TDocument, TField> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TItem> DistinctMany<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TItem> DistinctMany<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TItem>> DistinctManyAsync<TItem>(IClientSessionHandle session, FieldDefinition<TDocument, IEnumerable<TItem>> field, FilterDefinition<TDocument> filter, DistinctOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public IAsyncCursor<TResult> MapReduce<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IAsyncCursor<TResult> MapReduce<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IAsyncCursor<TResult>> MapReduceAsync<TResult>(IClientSessionHandle session, BsonJavaScript map, BsonJavaScript reduce, MapReduceOptions<TDocument, TResult>? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    public IFilteredMongoCollection<TDerivedDocument> OfType<TDerivedDocument>() where TDerivedDocument : TDocument => throw NotUsed();

    public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<TDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw NotUsed();

    private static NotSupportedException NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"{nameof(InMemoryMongoCollection<TDocument>)}.{member} is not supported by the in-memory test store.");
}

/// <summary>
/// Serialization, query-matching, update-application and aggregation-expression evaluation used by the
/// in-memory collection. Supports the subset of MongoDB query / update / pipeline operators exercised by
/// the Phone call and group-call handlers.
/// </summary>
internal static class BsonQueryEngine
{
    private static readonly IComparer<BsonValue> ValueComparer = Comparer<BsonValue>.Create(Compare);

    // ---- serialization ---------------------------------------------------------------------------

    public static BsonDocument ToBsonDocument(object document)
    {
        if (document is BsonDocument bd)
        {
            return (BsonDocument)bd.DeepClone();
        }

        var nominalType = document.GetType();
        var serializer = BsonSerializer.LookupSerializer(nominalType);
        var target = new BsonDocument();
        using var writer = new BsonDocumentWriter(target);
        var context = BsonSerializationContext.CreateRoot(writer);
        serializer.Serialize(context, new BsonSerializationArgs { NominalType = nominalType }, document);
        return target;
    }

    public static T Deserialize<T>(BsonDocument document)
    {
        if (typeof(T) == typeof(BsonDocument))
        {
            return (T)(object)(BsonDocument)document.DeepClone();
        }

        return BsonSerializer.Deserialize<T>(document);
    }

    // ---- rendering -------------------------------------------------------------------------------

    public static BsonDocument RenderFilter<T>(FilterDefinition<T>? filter)
    {
        if (filter == null)
        {
            return new BsonDocument();
        }

        return filter.Render(Args<T>());
    }

    public static BsonDocument RenderSort<T>(SortDefinition<T> sort) => sort.Render(Args<T>());

    public static BsonValue RenderUpdate<T>(UpdateDefinition<T> update) => update.Render(Args<T>());

    private static RenderArgs<T> Args<T>()
        => new(BsonSerializer.SerializerRegistry.GetSerializer<T>(), BsonSerializer.SerializerRegistry);

    // ---- sorting ---------------------------------------------------------------------------------

    public static IEnumerable<BsonDocument> ApplySort(IEnumerable<BsonDocument> source, BsonDocument sortDoc)
        => ApplySort(source, sortDoc, d => d);

    public static IEnumerable<T> ApplySort<T>(IEnumerable<T> source, BsonDocument sortDoc, Func<T, BsonDocument> selector)
    {
        if (sortDoc.ElementCount == 0)
        {
            return source;
        }

        IOrderedEnumerable<T>? ordered = null;
        foreach (var element in sortDoc)
        {
            var field = element.Name;
            var descending = element.Value.ToInt32() < 0;
            BsonValue Key(T item) => Resolve(selector(item), field).value;

            if (ordered == null)
            {
                ordered = descending ? source.OrderByDescending(Key, ValueComparer) : source.OrderBy(Key, ValueComparer);
            }
            else
            {
                ordered = descending ? ordered.ThenByDescending(Key, ValueComparer) : ordered.ThenBy(Key, ValueComparer);
            }
        }

        return ordered ?? source;
    }

    // ---- matching --------------------------------------------------------------------------------

    public static bool Matches(BsonDocument doc, BsonDocument filter)
    {
        foreach (var element in filter)
        {
            switch (element.Name)
            {
                case "$and":
                    if (!element.Value.AsBsonArray.All(x => Matches(doc, x.AsBsonDocument)))
                    {
                        return false;
                    }

                    break;
                case "$or":
                    if (!element.Value.AsBsonArray.Any(x => Matches(doc, x.AsBsonDocument)))
                    {
                        return false;
                    }

                    break;
                case "$nor":
                    if (element.Value.AsBsonArray.Any(x => Matches(doc, x.AsBsonDocument)))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!MatchField(doc, element.Name, element.Value))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private static bool MatchField(BsonDocument doc, string path, BsonValue condition)
    {
        var (present, value) = Resolve(doc, path);

        if (condition is BsonDocument cd && cd.ElementCount > 0 && cd.Names.All(n => n.StartsWith("$")))
        {
            return cd.All(op => MatchOperator(present, value, op.Name, op.Value));
        }

        return present && ValueEquals(value, condition);
    }

    private static bool MatchOperator(bool present, BsonValue value, string op, BsonValue opVal)
    {
        return op switch
        {
            "$eq" => present && ValueEquals(value, opVal),
            "$ne" => !(present && ValueEquals(value, opVal)),
            "$gt" => present && Compare(value, opVal) > 0,
            "$gte" => present && Compare(value, opVal) >= 0,
            "$lt" => present && Compare(value, opVal) < 0,
            "$lte" => present && Compare(value, opVal) <= 0,
            "$in" => present && opVal.AsBsonArray.Any(v => ValueEquals(value, v)),
            "$nin" => !(present && opVal.AsBsonArray.Any(v => ValueEquals(value, v))),
            "$exists" => opVal.ToBoolean() == present,
            "$elemMatch" => present && value is BsonArray arr &&
                            arr.OfType<BsonDocument>().Any(e => Matches(e, opVal.AsBsonDocument)),
            "$not" => !(opVal is BsonDocument nd && nd.All(o => MatchOperator(present, value, o.Name, o.Value))),
            _ => throw new NotSupportedException($"Unsupported query operator '{op}'.")
        };
    }

    private static (bool present, BsonValue value) Resolve(BsonDocument doc, string path)
    {
        BsonValue current = doc;
        foreach (var part in path.Split('.'))
        {
            if (current is BsonDocument d && d.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return (false, BsonNull.Value);
            }
        }

        return (true, current);
    }

    private static bool ValueEquals(BsonValue a, BsonValue b)
    {
        if (a.IsNumeric && b.IsNumeric)
        {
            return a.ToDouble().Equals(b.ToDouble());
        }

        return a.Equals(b);
    }

    private static int Compare(BsonValue? a, BsonValue? b)
    {
        a ??= BsonNull.Value;
        b ??= BsonNull.Value;
        if (a.IsNumeric && b.IsNumeric)
        {
            return a.ToDouble().CompareTo(b.ToDouble());
        }

        try
        {
            return a.CompareTo(b);
        }
        catch
        {
            return string.CompareOrdinal(a.ToString(), b.ToString());
        }
    }

    // ---- updates ---------------------------------------------------------------------------------

    public static void ApplyUpdate(BsonDocument doc, BsonValue rendered)
    {
        switch (rendered)
        {
            case BsonArray pipeline:
                ApplyPipeline(doc, pipeline);
                break;
            case BsonDocument operators:
                ApplyOperators(doc, operators);
                break;
            default:
                throw new NotSupportedException($"Unsupported update shape '{rendered.BsonType}'.");
        }
    }

    private static void ApplyOperators(BsonDocument doc, BsonDocument operators)
    {
        foreach (var op in operators)
        {
            var spec = op.Value.AsBsonDocument;
            switch (op.Name)
            {
                case "$set":
                    foreach (var field in spec)
                    {
                        SetPath(doc, field.Name, field.Value.DeepClone());
                    }

                    break;
                case "$setOnInsert":
                    break;
                case "$unset":
                    foreach (var field in spec)
                    {
                        UnsetPath(doc, field.Name);
                    }

                    break;
                case "$inc":
                    foreach (var field in spec)
                    {
                        var (_, current) = Resolve(doc, field.Name);
                        var baseValue = current.IsNumeric ? current.ToDouble() : 0d;
                        var integral = (!current.IsNumeric || current.IsInt32 || current.IsInt64) &&
                                       (field.Value.IsInt32 || field.Value.IsInt64);
                        SetPath(doc, field.Name, MakeNumber(baseValue + field.Value.ToDouble(), integral));
                    }

                    break;
                case "$push":
                    foreach (var field in spec)
                    {
                        Push(doc, field.Name, field.Value);
                    }

                    break;
                case "$addToSet":
                    foreach (var field in spec)
                    {
                        AddToSet(doc, field.Name, field.Value);
                    }

                    break;
                case "$pull":
                    foreach (var field in spec)
                    {
                        Pull(doc, field.Name, field.Value);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Unsupported update operator '{op.Name}'.");
            }
        }
    }

    private static void Push(BsonDocument doc, string path, BsonValue value)
    {
        var array = GetOrCreateArray(doc, path);
        if (value is BsonDocument eachDoc && eachDoc.Contains("$each"))
        {
            array.AddRange(eachDoc["$each"].AsBsonArray);
        }
        else
        {
            array.Add(value.DeepClone());
        }
    }

    private static void AddToSet(BsonDocument doc, string path, BsonValue value)
    {
        var array = GetOrCreateArray(doc, path);
        var items = value is BsonDocument eachDoc && eachDoc.Contains("$each")
            ? eachDoc["$each"].AsBsonArray.ToList()
            : new List<BsonValue> { value };
        foreach (var item in items)
        {
            if (!array.Any(existing => ValueEquals(existing, item)))
            {
                array.Add(item.DeepClone());
            }
        }
    }

    private static void Pull(BsonDocument doc, string path, BsonValue condition)
    {
        var (present, value) = Resolve(doc, path);
        if (!present || value is not BsonArray array)
        {
            return;
        }

        bool ShouldRemove(BsonValue element)
        {
            if (condition is BsonDocument cd && cd.ElementCount > 0 && cd.Names.All(n => n.StartsWith("$")))
            {
                return cd.All(op => MatchOperator(true, element, op.Name, op.Value));
            }

            return ValueEquals(element, condition);
        }

        var kept = new BsonArray(array.Where(e => !ShouldRemove(e)));
        SetPath(doc, path, kept);
    }

    // ---- aggregation pipeline updates ------------------------------------------------------------

    private static void ApplyPipeline(BsonDocument doc, BsonArray stages)
    {
        foreach (var stage in stages)
        {
            var stageDoc = stage.AsBsonDocument;
            foreach (var stageElement in stageDoc)
            {
                if (stageElement.Name is "$set" or "$addFields")
                {
                    foreach (var field in stageElement.Value.AsBsonDocument)
                    {
                        var value = Eval(field.Value, doc, new Dictionary<string, BsonValue>());
                        SetPath(doc, field.Name, value);
                    }
                }
                else
                {
                    throw new NotSupportedException($"Unsupported pipeline stage '{stageElement.Name}'.");
                }
            }
        }
    }

    private static BsonValue Eval(BsonValue expr, BsonValue root, Dictionary<string, BsonValue> vars)
    {
        switch (expr.BsonType)
        {
            case BsonType.String:
                var s = expr.AsString;
                if (s == "$$ROOT")
                {
                    return root;
                }

                if (s.StartsWith("$$"))
                {
                    var body = s[2..];
                    var dot = body.IndexOf('.');
                    var name = dot < 0 ? body : body[..dot];
                    var path = dot < 0 ? string.Empty : body[(dot + 1)..];
                    var value = vars.TryGetValue(name, out var v) ? v : BsonNull.Value;
                    return ResolveExprPath(value, path);
                }

                if (s.StartsWith("$"))
                {
                    return ResolveExprPath(root, s[1..]);
                }

                return expr;
            case BsonType.Document:
                var d = expr.AsBsonDocument;
                if (d.ElementCount >= 1 && d.Names.Any(n => n.StartsWith("$")))
                {
                    return EvalOperator(d, root, vars);
                }

                return d.DeepClone();
            case BsonType.Array:
                return new BsonArray(expr.AsBsonArray.Select(x => Eval(x, root, vars)));
            default:
                return expr;
        }
    }

    private static BsonValue EvalOperator(BsonDocument d, BsonValue root, Dictionary<string, BsonValue> vars)
    {
        var operatorElement = d.First(e => e.Name.StartsWith("$"));
        var op = operatorElement.Name;
        var val = operatorElement.Value;
        BsonValue Ev(BsonValue e) => Eval(e, root, vars);

        switch (op)
        {
            case "$literal":
                return val;
            case "$ifNull":
            {
                var arr = val.AsBsonArray;
                var first = Ev(arr[0]);
                return IsNullish(first) ? Ev(arr[1]) : first;
            }
            case "$concatArrays":
            {
                var result = new BsonArray();
                foreach (var operand in val.AsBsonArray)
                {
                    if (Ev(operand) is BsonArray ba)
                    {
                        result.AddRange(ba);
                    }
                }

                return result;
            }
            case "$filter":
            {
                var spec = val.AsBsonDocument;
                var input = Ev(spec["input"]);
                var asName = spec.Contains("as") ? spec["as"].AsString : "this";
                var cond = spec["cond"];
                var output = new BsonArray();
                if (input is BsonArray inputArray)
                {
                    foreach (var element in inputArray)
                    {
                        var scoped = new Dictionary<string, BsonValue>(vars) { [asName] = element };
                        if (Truthy(Eval(cond, root, scoped)))
                        {
                            output.Add(element);
                        }
                    }
                }

                return output;
            }
            case "$map":
            {
                var spec = val.AsBsonDocument;
                var input = Ev(spec["input"]);
                var asName = spec.Contains("as") ? spec["as"].AsString : "this";
                var inExpr = spec["in"];
                var output = new BsonArray();
                if (input is BsonArray inputArray)
                {
                    foreach (var element in inputArray)
                    {
                        var scoped = new Dictionary<string, BsonValue>(vars) { [asName] = element };
                        output.Add(Eval(inExpr, root, scoped));
                    }
                }

                return output;
            }
            case "$add":
            {
                var arr = val.AsBsonArray;
                double sum = 0;
                var integral = true;
                foreach (var operand in arr)
                {
                    var v = Ev(operand);
                    if (!(v.IsInt32 || v.IsInt64))
                    {
                        integral = false;
                    }

                    sum += v.IsNumeric ? v.ToDouble() : 0d;
                }

                return MakeNumber(sum, integral);
            }
            case "$subtract":
            {
                var arr = val.AsBsonArray;
                var a = Ev(arr[0]);
                var b = Ev(arr[1]);
                var integral = (a.IsInt32 || a.IsInt64) && (b.IsInt32 || b.IsInt64);
                return MakeNumber((a.IsNumeric ? a.ToDouble() : 0d) - (b.IsNumeric ? b.ToDouble() : 0d), integral);
            }
            case "$not":
            {
                var arg = val is BsonArray notArr ? notArr[0] : val;
                return !Truthy(Ev(arg));
            }
            case "$and":
                return val.AsBsonArray.All(e => Truthy(Ev(e)));
            case "$or":
                return val.AsBsonArray.Any(e => Truthy(Ev(e)));
            case "$eq":
                return ValueEquals(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1]));
            case "$ne":
                return !ValueEquals(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1]));
            case "$gt":
                return Compare(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1])) > 0;
            case "$gte":
                return Compare(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1])) >= 0;
            case "$lt":
                return Compare(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1])) < 0;
            case "$lte":
                return Compare(Ev(val.AsBsonArray[0]), Ev(val.AsBsonArray[1])) <= 0;
            case "$in":
            {
                var needle = Ev(val.AsBsonArray[0]);
                return Ev(val.AsBsonArray[1]) is BsonArray candidates && candidates.Any(x => ValueEquals(needle, x));
            }
            case "$cond":
            {
                BsonValue ifExpr, thenExpr, elseExpr;
                if (val is BsonArray condArr)
                {
                    ifExpr = condArr[0];
                    thenExpr = condArr[1];
                    elseExpr = condArr[2];
                }
                else
                {
                    var condDoc = val.AsBsonDocument;
                    ifExpr = condDoc["if"];
                    thenExpr = condDoc["then"];
                    elseExpr = condDoc["else"];
                }

                return Truthy(Ev(ifExpr)) ? Ev(thenExpr) : Ev(elseExpr);
            }
            case "$mergeObjects":
            {
                var merged = new BsonDocument();
                foreach (var operand in val.AsBsonArray)
                {
                    if (Ev(operand) is BsonDocument od)
                    {
                        merged.Merge(od, true);
                    }
                }

                return merged;
            }
            default:
                throw new NotSupportedException($"Unsupported aggregation operator '{op}'.");
        }
    }

    private static BsonValue ResolveExprPath(BsonValue value, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return value;
        }

        var current = value;
        foreach (var part in path.Split('.'))
        {
            if (current is BsonDocument d && d.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return BsonNull.Value;
            }
        }

        return current;
    }

    private static bool Truthy(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Boolean => value.AsBoolean,
            BsonType.Null or BsonType.Undefined => false,
            _ when value.IsNumeric => value.ToDouble() != 0d,
            _ => true
        };
    }

    private static bool IsNullish(BsonValue value) => value == null || value.IsBsonNull || value.IsBsonUndefined;

    private static BsonValue MakeNumber(double value, bool integral)
    {
        if (!integral)
        {
            return new BsonDouble(value);
        }

        if (value >= int.MinValue && value <= int.MaxValue && value == Math.Floor(value))
        {
            return new BsonInt32((int)value);
        }

        return new BsonInt64((long)value);
    }

    private static BsonArray GetOrCreateArray(BsonDocument doc, string path)
    {
        var (present, value) = Resolve(doc, path);
        if (present && value is BsonArray array)
        {
            return array;
        }

        var created = new BsonArray();
        SetPath(doc, path, created);
        return created;
    }

    private static void SetPath(BsonDocument doc, string path, BsonValue value)
    {
        var parts = path.Split('.');
        var current = doc;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) || next is not BsonDocument nested)
            {
                nested = new BsonDocument();
                current[parts[i]] = nested;
            }

            current = nested;
        }

        current[parts[^1]] = value;
    }

    private static void UnsetPath(BsonDocument doc, string path)
    {
        var parts = path.Split('.');
        var current = doc;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) || next is not BsonDocument nested)
            {
                return;
            }

            current = nested;
        }

        current.Remove(parts[^1]);
    }
}
