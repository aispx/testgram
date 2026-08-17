using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Push;
using MyTelegram.Push.Tests.Infrastructure;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 31: Round-trip of setting and clearing the device lock.
//
// For any PermAuthKeyId and Period > 0, after DeviceLockStore.SetAsync(permAuthKeyId, Period) the lock
// is active (IsLockedAsync == true); a subsequent SetAsync(permAuthKeyId, 0) clears it
// (IsLockedAsync == false). The store is backed by an in-memory fake IConnectionMultiplexer/IDatabase
// so the property runs without a real Redis server, modelling StringSet (with TTL), KeyExists and
// KeyDelete on the key push:locked:{authKeyId:x}.
//
// Validates: Requirements 9.1, 9.3
public class Property31_DeviceLockRoundTripTests
{
    /// <summary>Positive 64-bit auth key id, reusing the catalogue's identifier range.</summary>
    private static Gen<long> PermAuthKeyId => PushGen.PositiveId;

    /// <summary>A strictly positive lock period in seconds.</summary>
    private static Gen<int> PositivePeriod => Gen.Choose(1, 86_400);

    // Property 31: Round-trip of setting and clearing the device lock
    // Validates: Requirements 9.1, 9.3
    [Property(MaxTest = 20)]
    public Property Set_then_clear_round_trips_lock_state()
    {
        return Prop.ForAll(Arb.From(PermAuthKeyId), Arb.From(PositivePeriod), (permAuthKeyId, period) =>
        {
            var store = new DeviceLockStore(
                FakeRedis.CreateConnectionMultiplexer(),
                NullLogger<DeviceLockStore>.Instance);

            // A fresh auth key is not locked.
            var initiallyLocked = store.IsLockedAsync(permAuthKeyId).GetAwaiter().GetResult();

            // Set(period > 0) => the lock becomes active.
            store.SetAsync(permAuthKeyId, period).GetAwaiter().GetResult();
            var lockedAfterSet = store.IsLockedAsync(permAuthKeyId).GetAwaiter().GetResult();

            // Set(0) => the lock is cleared.
            store.SetAsync(permAuthKeyId, 0).GetAwaiter().GetResult();
            var lockedAfterClear = store.IsLockedAsync(permAuthKeyId).GetAwaiter().GetResult();

            return (!initiallyLocked && lockedAfterSet && !lockedAfterClear)
                .Label($"permAuthKeyId={permAuthKeyId}, period={period}, " +
                       $"initiallyLocked={initiallyLocked}, lockedAfterSet={lockedAfterSet}, " +
                       $"lockedAfterClear={lockedAfterClear}");
        });
    }
}
