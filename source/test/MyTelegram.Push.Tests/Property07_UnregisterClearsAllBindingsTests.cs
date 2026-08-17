// Feature: push-updates, Property 7: Unregister clears the binding to all OtherUids and to the current account.
//
// For any device registered with a set of OtherUids and a current UserId, after
// account.unregisterDevice none of the accounts in OtherUids ∪ {UserId} get that Token when querying
// their push devices.
//
// The aggregate is keyed by Token (PushDeviceId.Create(token)) and the read model is deleted on
// unregistration (ApplyAsync(PushDeviceUnRegisteredEvent) calls context.MarkForDeletion()). Deleting
// the single read-model record removes the binding for every account the device was addressable to,
// because multi-account routing resolves recipients via the GetPushDevicesForRecipientQuery predicate
// `UserId == id || OtherUids.Contains(id)`. This test drives the real PushDeviceAggregate for both
// register and unregister, projects the emitted events into an in-memory model of the read-model
// store, and asserts the query predicate returns nothing for the device's Token for every account in
// OtherUids ∪ {UserId}.
//
// Validates: Requirements 3.2

using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property07_UnregisterClearsAllBindingsTests
{
    private const long BaseDateMs = 1_700_000_000_000L;

    // Property 7: Unregister clears the binding to all OtherUids and to the current account
    // Validates: Requirements 3.2
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Unregister_clears_binding_for_all_other_uids_and_current_account(DeviceRegistration reg)
    {
        // The set of accounts the device must be addressable to before unregistration: the owner plus
        // every multi-account id (OtherUids ∪ {UserId}).
        var boundAccounts = reg.OtherUids.Append(reg.UserId).Distinct().ToList();

        // In-memory model of the PushDeviceReadModel store, keyed by Id (= Token), exactly as the
        // production store keys it. Projecting the aggregate's events into this store mirrors the
        // read-model projection (register => upsert, unregister => delete).
        var store = new Dictionary<string, FakePushDeviceReadModel>(StringComparer.Ordinal);

        var aggregate = new PushDeviceAggregate(PushDeviceId.Create(reg.Token, reg.UserId));

        // --- Register -------------------------------------------------------------------------
        aggregate.RegisterDevice(
            RequestInfo.Empty with { UserId = reg.UserId, PermAuthKeyId = reg.PermAuthKeyId, Date = BaseDateMs },
            reg.UserId,
            reg.PermAuthKeyId,
            reg.TokenType,
            reg.Token,
            reg.NoMuted,
            reg.AppSandbox,
            reg.Secret,
            reg.OtherUids);

        ProjectInto(store, aggregate);

        // Sanity: before unregistration the device is addressable to every account in
        // OtherUids ∪ {UserId}. (If this did not hold, the post-condition would be vacuous.)
        foreach (var account in boundAccounts)
        {
            QueryDevicesForRecipient(store, account)
                .ShouldContain(d => string.Equals(d.Token, reg.Token, StringComparison.Ordinal),
                    $"device should be addressable to account {account} before unregister");
        }

        // --- Unregister -----------------------------------------------------------------------
        aggregate.UnRegisterDevice(
            RequestInfo.Empty with { UserId = reg.UserId, PermAuthKeyId = reg.PermAuthKeyId, Date = BaseDateMs + 1 },
            reg.TokenType,
            reg.Token,
            reg.OtherUids.ToList());

        ProjectInto(store, aggregate);

        // --- Assert ---------------------------------------------------------------------------
        // After unregistration no account in OtherUids ∪ {UserId} resolves the device's Token.
        foreach (var account in boundAccounts)
        {
            QueryDevicesForRecipient(store, account)
                .ShouldNotContain(d => string.Equals(d.Token, reg.Token, StringComparison.Ordinal),
                    $"account {account} must not resolve the token after unregister");
        }
    }

    /// <summary>
    /// Folds the aggregate's uncommitted events into the in-memory read-model store: a register event
    /// upserts the device record (keyed by Token), an unregister event deletes it (MarkForDeletion).
    /// </summary>
    private static void ProjectInto(IDictionary<string, FakePushDeviceReadModel> store, PushDeviceAggregate aggregate)
    {
        foreach (var aggregateEvent in aggregate.UncommittedEvents.Select(e => e.AggregateEvent))
        {
            switch (aggregateEvent)
            {
                case PushDeviceRegisteredEvent r:
                    store[aggregate.Id.Value] = new FakePushDeviceReadModel
                    {
                        Id = aggregate.Id.Value,
                        UserId = r.UserId,
                        PermAuthKeyId = r.PermAuthKeyId,
                        TokenType = r.TokenType,
                        Token = r.Token,
                        Secret = r.Secret,
                        NoMuted = r.NoMuted,
                        AppSandbox = r.AppSandbox,
                        OtherUids = r.OtherUids
                    };
                    break;
                case PushDeviceUnRegisteredEvent:
                    store.Remove(aggregate.Id.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// In-memory equivalent of GetPushDevicesForRecipientQueryHandler: a device is addressable to a
    /// recipient when it is owned by them (UserId) or lists them in OtherUids.
    /// </summary>
    private static IReadOnlyList<FakePushDeviceReadModel> QueryDevicesForRecipient(
        IDictionary<string, FakePushDeviceReadModel> store,
        long recipientUserId) =>
        store.Values
            .Where(p => p.UserId == recipientUserId ||
                        (p.OtherUids != null && p.OtherUids.Contains(recipientUserId)))
            .ToList();
}
