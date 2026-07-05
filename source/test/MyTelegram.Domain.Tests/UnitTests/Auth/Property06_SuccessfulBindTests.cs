// Feature: auth-methods-completion, Property 6: A successful bind records the exact ids and expiry and overwrites prior bindings
//
// For any sequence of one or more valid bind requests against the same Perm_Auth_Key,
// bindTempAuthKey returns TBoolTrue and the stored binding equals the LAST request's
// (temp_auth_key_id, perm_auth_key_id, expires_at), with no trace of earlier temp keys.
//
// Validates: Requirements 3.1, 3.2, 3.3
//
// Approach: this property targets the domain aggregate directly (DeviceAggregate.BindTempAuthKey,
// which emits DeviceTempAuthKeyBoundEvent, applied by DeviceState to set TempAuthKeyId ---
// overwriting any prior binding --- and TempAuthKeyExpiresAt). For each generated case a fresh
// DeviceAggregate is constructed and driven into the "created" state by applying a
// DeviceCreatedEvent (which seeds a genuine prior temp-key binding: an initial temp key id and
// perm key id). Then a generated NON-EMPTY sequence of bind operations is applied by calling the
// public aggregate method BindTempAuthKey once per operation. Because each bind overwrites the
// single stored temp-key binding, applying two or more binds exercises the overwrite path
// (Requirement 3.2), and the last bind's ids/expiry must be exactly what the state retains
// (Requirements 3.1, 3.3). The property reads the aggregate's private DeviceState (via reflection)
// and asserts the final TempAuthKeyId/TempAuthKeyExpiresAt equal the LAST bind's values, the
// PermAuthKeyId is unchanged, and --- when the last temp key differs from the seeded/earlier ones
// --- no trace of an earlier temp key remains.

using System.Reflection;
using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.Device;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property06_SuccessfulBindTests
{
    // Property 6: A successful bind records the exact ids and expiry and overwrites prior bindings
    // Validates: Requirements 3.1, 3.2, 3.3
    [Property(Arbitrary = new[] { typeof(BindSequenceArbitraries) }, MaxTest = 100)]
    public void Successful_bind_records_last_ids_and_expiry_overwriting_prior_bindings(BindSequenceCase testCase)
    {
        // Arrange: a fresh Device aggregate driven into the "created" state (so BindTempAuthKey's
        // AggregateIsCreated precondition holds) with a genuine prior temp-key binding seeded by
        // the DeviceCreatedEvent -- exactly the shape DeviceAggregate.CreateDevice emits.
        var now = DateTime.UtcNow.ToTimestamp();
        var aggregateId = DeviceId.Create(testCase.PermAuthKeyId);

        var createdEvent = new DeviceCreatedEvent(
            isNewDevice: true,
            permAuthKeyId: testCase.PermAuthKeyId,
            tempAuthKeyId: testCase.InitialTempAuthKeyId,
            userId: 1L,
            apiId: 1,
            appName: "app",
            appVersion: "1.0",
            hash: 123L,
            officialApp: false,
            passwordPending: false,
            deviceModel: "model",
            platform: "platform",
            systemVersion: "1.0",
            systemLangCode: "en",
            langPack: "",
            langCode: "en",
            ip: "127.0.0.1",
            layer: 0,
            date: now,
            parameters: null);

        var aggregate = new DeviceAggregate(aggregateId);
        aggregate.ApplyEvents(new IDomainEvent[]
        {
            new DomainEvent<DeviceAggregate, DeviceId, DeviceCreatedEvent>(
                createdEvent, Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 1)
        });

        // Act: apply the non-empty sequence of valid bind requests against the same Perm_Auth_Key.
        // Each call emits DeviceTempAuthKeyBoundEvent, which the state applies immediately,
        // overwriting the single stored temp-key binding.
        foreach (var bind in testCase.Binds)
        {
            aggregate.BindTempAuthKey(testCase.PermAuthKeyId, bind.TempAuthKeyId, bind.ExpiresAt);
        }

        // Assert: the stored binding equals the LAST request's (temp_auth_key_id, perm_auth_key_id,
        // expires_at), with no trace of earlier temp keys.
        var lastBind = testCase.Binds[^1];
        var state = GetState(aggregate);

        // Requirement 3.1 / 3.2: the stored temp key id is exactly the last bind's id (each bind
        // overwrites the previous binding, including the seeded prior binding).
        state.TempAuthKeyId.ShouldBe(lastBind.TempAuthKeyId);
        // Requirement 3.3: the recorded expiry is exactly the last bind's expires_at.
        state.TempAuthKeyExpiresAt.ShouldBe(lastBind.ExpiresAt);
        // The perm key association is unchanged (binding is against the same Perm_Auth_Key).
        state.PermAuthKeyId.ShouldBe(testCase.PermAuthKeyId);

        // No trace of earlier temp keys: whenever the last bind's temp key differs from an earlier
        // one (the seeded prior binding or any prior bind in the sequence), that earlier key is no
        // longer stored -- the single stored value equals only the last bind's id (asserted above).
        if (lastBind.TempAuthKeyId != testCase.InitialTempAuthKeyId)
        {
            state.TempAuthKeyId.ShouldNotBe(testCase.InitialTempAuthKeyId);
        }
    }

    /// <summary>Reads the aggregate's private DeviceState so the stored temp-key binding can be
    /// inspected (the state exposes TempAuthKeyId / TempAuthKeyExpiresAt / PermAuthKeyId as public
    /// getters).</summary>
    private static DeviceState GetState(DeviceAggregate aggregate)
    {
        var field = typeof(DeviceAggregate).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (DeviceState)field.GetValue(aggregate)!;
    }
}

/// <summary>A single valid bind request: the temp auth key id to bind and its expiry timestamp.</summary>
public sealed record BindOp(long TempAuthKeyId, int ExpiresAt);

/// <summary>Input case for Property 6: the perm auth key the binds target, a seeded prior temp-key
/// binding (established by the DeviceCreatedEvent so the overwrite of an existing binding is
/// exercised), and a NON-EMPTY sequence of bind requests applied in order.</summary>
public sealed record BindSequenceCase(
    long PermAuthKeyId,
    long InitialTempAuthKeyId,
    IReadOnlyList<BindOp> Binds);

/// <summary>FsCheck arbitrary surface for Property 6. Generates a positive perm auth key id, a
/// positive seeded prior temp key id, and a non-empty list of bind operations (each a positive temp
/// key id plus a positive expiry timestamp). Sequences of length >= 2 exercise the overwrite path
/// (Requirement 3.2); every case exercises the exact-record path (Requirements 3.1, 3.3).</summary>
public static class BindSequenceArbitraries
{
    public static Arbitrary<BindSequenceCase> BindSequenceCase()
    {
        var permGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        var tempGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        var expireGen = Gen.Choose(1, int.MaxValue);

        var bindGen =
            from t in tempGen
            from e in expireGen
            select new BindOp(t, e);

        var gen =
            from perm in permGen
            from initialTemp in tempGen
            from binds in Gen.NonEmptyListOf(bindGen)
            select new BindSequenceCase(perm, initialTemp, binds.ToArray());

        return Arb.From(gen);
    }
}
