// Feature: push-updates, Property 33: Routing by OtherUids picks a device for any member.
//
// For any device registered with an owner UserId and a set of OtherUids, the recipient push-device
// query (GetPushDevicesForRecipientQueryHandler) returns that device if and only if the recipient is
// a member of OtherUids ∪ {UserId}. This biconditional is exercised by driving the production
// InMemory query handler over an in-memory IQueryOnlyReadModelStore populated with arbitrary device
// fixtures (overlapping tokens, overlapping OtherUids), and probing it with recipients that are
// guaranteed members (every owner, every OtherUid) as well as recipients that are guaranteed
// non-members (ids outside the generator's pool).
//
// Validates: Requirements 10.2

using System.Linq.Expressions;
using EventFlow.Aggregates;
using EventFlow.ReadStores;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.EventFlow.ReadStores;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.QueryHandlers.InMemory.Device;
using MyTelegram.Queries;
using Shouldly;
using InMemoryPushDeviceReadModel = MyTelegram.ReadModel.InMemory.PushDeviceReadModel;

namespace MyTelegram.Push.Tests;

public class Property33_RecipientRoutingByOtherUidsTests
{
    // Property 33: Routing by OtherUids picks a device for any member
    // Validates: Requirements 10.2
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Recipient_query_selects_device_iff_recipient_in_otherUids_union_owner(DeviceSet deviceSet)
    {
        // Arrange: materialise each generated fixture into a real (event-sourced) InMemory read model
        // and feed it to the PRODUCTION InMemory query handler via an in-memory read-model store, so
        // the handler's own predicate (UserId == recipient || OtherUids.Contains(recipient)) is the
        // code under test.
        var devices = deviceSet.Devices.Select(BuildReadModel).ToList();
        var store = new InMemoryQueryOnlyStore<InMemoryPushDeviceReadModel>(devices);
        var handler = new GetPushDevicesForRecipientQueryHandler(store);

        // Candidate recipients: every owner and every OtherUid (guaranteed members of at least one
        // device), the generator's recipient (member or not), plus ids that cannot be present.
        var candidates = new HashSet<long>(
            devices.Select(d => d.UserId)
                .Concat(devices.SelectMany(d => d.OtherUids ?? Array.Empty<long>())))
        {
            deviceSet.RecipientUserId,
            -1,
            0,
            long.MaxValue
        };

        foreach (var recipient in candidates)
        {
            var result = handler
                .ExecuteQueryAsync(new GetPushDevicesForRecipientQuery(recipient), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            // Compare by identity so duplicate Tokens across fixtures are handled correctly.
            var returned = new HashSet<InMemoryPushDeviceReadModel>(result.Cast<InMemoryPushDeviceReadModel>());

            foreach (var device in devices)
            {
                var isMember = device.UserId == recipient ||
                               (device.OtherUids ?? Array.Empty<long>()).Contains(recipient);

                returned.Contains(device).ShouldBe(
                    isMember,
                    $"recipient={recipient}, owner={device.UserId}, " +
                    $"otherUids=[{string.Join(",", device.OtherUids ?? Array.Empty<long>())}]");
            }
        }
    }

    /// <summary>
    /// Builds a real <see cref="InMemoryPushDeviceReadModel"/> by applying a
    /// <see cref="PushDeviceRegisteredEvent"/>, mirroring how the production read-model projection
    /// populates the (private-setter) model from domain events.
    /// </summary>
    private static InMemoryPushDeviceReadModel BuildReadModel(FakePushDeviceReadModel fake)
    {
        var registeredEvent = new PushDeviceRegisteredEvent(
            RequestInfo.Empty with
            {
                UserId = fake.UserId,
                PermAuthKeyId = fake.PermAuthKeyId,
                Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            fake.UserId,
            fake.PermAuthKeyId,
            fake.TokenType,
            fake.Token,
            fake.NoMuted,
            fake.AppSandbox,
            fake.Secret,
            fake.OtherUids);

        var readModel = new InMemoryPushDeviceReadModel();
        var domainEvent = new DomainEvent<PushDeviceAggregate, PushDeviceId, PushDeviceRegisteredEvent>(
            registeredEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            PushDeviceId.Create(fake.Token, fake.UserId),
            1);
        readModel.ApplyAsync(null!, domainEvent, CancellationToken.None).GetAwaiter().GetResult();
        return readModel;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IQueryOnlyReadModelStore{T}"/> that evaluates the handler's LINQ
    /// predicate against a fixed list. Only the <see cref="FindAsync"/> overload used by the
    /// production handler is implemented; the others are unused by this test.
    /// </summary>
    private sealed class InMemoryQueryOnlyStore<TReadModel> : IQueryOnlyReadModelStore<TReadModel>
        where TReadModel : class, IReadModel
    {
        private readonly List<TReadModel> _items;

        public InMemoryQueryOnlyStore(IEnumerable<TReadModel> items) => _items = items.ToList();

        public IQueryable<TReadModel> GetAll() => _items.AsQueryable();

        public Task<IReadOnlyCollection<TReadModel>> FindAsync(
            Expression<Func<TReadModel, bool>> filter,
            int skip = 0,
            int limit = 0,
            SortOptions<TReadModel>? sort = null,
            CancellationToken cancellationToken = default)
        {
            var predicate = filter.Compile();
            IReadOnlyCollection<TReadModel> result = _items.Where(predicate).ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyCollection<TResult>> FindAsync<TResult>(
            Expression<Func<TReadModel, bool>> filter,
            Expression<Func<TReadModel, TResult>> createResult,
            int skip = 0,
            int limit = 0,
            SortOptions<TReadModel>? sort = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TReadModel?> FirstOrDefaultAsync(
            Expression<Func<TReadModel, bool>> filter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TResult?> FirstOrDefaultAsync<TResult>(
            Expression<Func<TReadModel, bool>> filter,
            Expression<Func<TReadModel, TResult>> createResult,
            SortOptions<TReadModel>? sort = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> CountAsync(
            Expression<Func<TReadModel, bool>> filter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<List<TResult>> GroupByAsync<TKey, TResult>(
            Expression<Func<TReadModel, bool>>? filter,
            Expression<Func<TReadModel, TKey>> keySelector,
            Expression<Func<IGrouping<TKey, TReadModel>, TResult>> resultSelector) => throw new NotSupportedException();
    }
}
