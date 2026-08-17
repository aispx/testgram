using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Push.Tests.Infrastructure;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 5: Re-registration is idempotent within 24 hours.
//
// For any valid registration request, applying it once versus applying it twice with identical
// parameters within 24 hours yields an identical final read-model state (no duplicate device).
//
// The aggregate is keyed by token (PushDeviceId.Create(token)) and keys LastRegisteredAt off
// RequestInfo.Date (unix ms). A second identical registration whose request date is < 24h after the
// first must be a no-op: PushDeviceAggregate.RegisterDevice emits no additional
// PushDeviceRegisteredEvent, so the projected read-model state is byte-for-byte identical to a single
// registration and there is exactly one device.
public class Property05_RegistrationIdempotencyTests
{
    // Fixed base instant (unix ms) the scenarios register at.
    private const long BaseDateMs = 1_700_000_000_000L;

    private const long TwentyFourHoursMs = 24L * 60 * 60 * 1000;

    /// <summary>A second-registration offset strictly inside the 24h re-registration window.</summary>
    private static Gen<long> WithinWindowDeltaMs =>
        Gen.Choose(0, (int)(TwentyFourHoursMs - 1)).Select(i => (long)i);

    // Property 5: Re-registration is idempotent within 24 hours
    // Validates: Requirements 1.4
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public Property Repeated_registration_within_24h_is_idempotent(DeviceRegistration registration)
    {
        return Prop.ForAll(Arb.From(WithinWindowDeltaMs), deltaMs =>
        {
            // Apply once.
            var once = RegisterAll(registration, BaseDateMs);

            // Apply twice with identical parameters, the second call < 24h after the first.
            var twice = RegisterAll(registration, BaseDateMs, BaseDateMs + deltaMs);

            // The second identical registration within 24h must be a no-op (no duplicate event/device).
            var onceRegisterCount = CountRegisterEvents(once);
            var twiceRegisterCount = CountRegisterEvents(twice);

            // Final projected read-model state must be identical for "once" and "twice".
            var onceState = Project(once);
            var twiceState = Project(twice);

            return (onceRegisterCount == 1
                    && twiceRegisterCount == 1
                    && StatesEqual(onceState, twiceState))
                .Label($"onceRegisterCount={onceRegisterCount}, twiceRegisterCount={twiceRegisterCount}, " +
                       $"deltaMs={deltaMs}");
        });
    }

    private static PushDeviceAggregate RegisterAll(DeviceRegistration r, params long[] dateMs)
    {
        var aggregate = new PushDeviceAggregate(PushDeviceId.Create(r.Token, r.UserId));
        foreach (var date in dateMs)
        {
            aggregate.RegisterDevice(
                RequestInfo.Empty with { Date = date },
                r.UserId,
                r.PermAuthKeyId,
                r.TokenType,
                r.Token,
                r.NoMuted,
                r.AppSandbox,
                r.Secret,
                r.OtherUids);
        }

        return aggregate;
    }

    private static int CountRegisterEvents(PushDeviceAggregate aggregate) =>
        aggregate.UncommittedEvents
            .Select(e => e.AggregateEvent)
            .OfType<PushDeviceRegisteredEvent>()
            .Count();

    /// <summary>
    /// Folds the aggregate's uncommitted events into the read-model state they would produce: each
    /// register event overwrites the device fields; an unregister event deletes it (null).
    /// </summary>
    private static ProjectedDevice? Project(PushDeviceAggregate aggregate)
    {
        ProjectedDevice? state = null;
        foreach (var aggregateEvent in aggregate.UncommittedEvents.Select(e => e.AggregateEvent))
        {
            switch (aggregateEvent)
            {
                case PushDeviceRegisteredEvent r:
                    state = new ProjectedDevice(
                        aggregate.Id.Value,
                        r.UserId,
                        r.PermAuthKeyId,
                        r.TokenType,
                        r.Token,
                        r.NoMuted,
                        r.AppSandbox,
                        r.Secret,
                        r.OtherUids);
                    break;
                case PushDeviceUnRegisteredEvent:
                    state = null;
                    break;
            }
        }

        return state;
    }

    private static bool StatesEqual(ProjectedDevice? a, ProjectedDevice? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Id == b.Id
               && a.UserId == b.UserId
               && a.PermAuthKeyId == b.PermAuthKeyId
               && a.TokenType == b.TokenType
               && string.Equals(a.Token, b.Token, StringComparison.Ordinal)
               && a.NoMuted == b.NoMuted
               && a.AppSandbox == b.AppSandbox
               && SecretEquals(a.Secret, b.Secret)
               && OtherUidsEqual(a.OtherUids, b.OtherUids);
    }

    private static bool SecretEquals(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool OtherUidsEqual(IReadOnlyList<long>? left, IReadOnlyList<long>? right)
    {
        var leftEmpty = left is null || left.Count == 0;
        var rightEmpty = right is null || right.Count == 0;
        if (leftEmpty && rightEmpty)
        {
            return true;
        }

        if (leftEmpty || rightEmpty || left!.Count != right!.Count)
        {
            return false;
        }

        return !left.Where((t, i) => t != right[i]).Any();
    }

    private sealed record ProjectedDevice(
        string Id,
        long UserId,
        long PermAuthKeyId,
        int TokenType,
        string Token,
        bool NoMuted,
        bool AppSandbox,
        byte[]? Secret,
        IReadOnlyList<long>? OtherUids);
}
