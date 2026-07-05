// Feature: push-updates, Property 33: Маршрутизация по OtherUids выбирает устройство для любого члена.
// For any set of devices registered with arbitrary owner UserId and OtherUids, the recipient
// push-devices query (GetPushDevicesForRecipientQueryHandler) returns a device if and only if the
// recipient belongs to OtherUids ∪ {UserId}. This biconditional is asserted over arbitrary
// recipients that are guaranteed members (each device owner, each OtherUid) as well as recipients
// that are guaranteed non-members.
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

public class Property33_OtherUidsRoutingTests
{
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Recipient_query_returns_device_iff_recipient_in_otherUids_union_owner(DeviceSet deviceSet)
    {
        // Arrange: materialise each generated fixture into a real (event-sourced) read model and feed
        // it to the production InMemory query handler via an in-memory read-model store.
        var devices = deviceSet.Devices.Select(BuildReadModel).ToList();
        var store = new InMemoryQueryOnlyStore<InMemoryPushDeviceReadModel>(devices);
        var handler = new GetPushDevicesForRecipientQueryHandler(store);

        // Candidate recipients: every device owner and every OtherUid (guaranteed members), the
        // generator's recipient (member or non-member), plus ids that cannot be present (non-members).
        var presentIds = devices.Select(d => d.UserId)
            .Concat(devices.SelectMany(d => d.OtherUids ?? Array.Empty<long>()))
            .ToHashSet();
        var candidates = new HashSet<long>(presentIds) { deviceSet.RecipientUserId };
        // Add non-members: ids outside the pool the generators draw from (1..20 owners, +100/+1000 etc.).
        candidates.Add(-1);
        candidates.Add(0);
        candidates.Add(9_999_999);

        foreach (var recipient in candidates)
        {
            var result = handler
                .ExecuteQueryAsync(new GetPushDevicesForRecipientQuery(recipient), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            // Result references resolve back to the stored devices; compare by identity so duplicate
            // tokens are handled correctly.
            var returned = new HashSet<InMemoryPushDeviceReadModel>(result.Cast<InMemoryPushDeviceReadModel>());

            // Biconditional (Req 10.2): a device is returned IFF the recipient is in OtherUids ∪ {UserId}.
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
            PushDeviceId.Create(fake.Token),
            1);
        readModel.ApplyAsync(null!, domainEvent, CancellationToken.None).GetAwaiter().GetResult();
        return readModel;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IQueryOnlyReadModelStore{T}"/> that evaluates the handler's LINQ
    /// predicate against a fixed list. Only <see cref="FindAsync"/> (the overload used by the
    /// production handler) is implemented; the rest are unused by this test.
    /// </summary>
    private sealed class InMemoryQueryOnlyStore<TReadModel> : IQueryOnlyReadModelStore<TReadModel>
        where TReadModel : class, global::EventFlow.ReadStores.IReadModel
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
