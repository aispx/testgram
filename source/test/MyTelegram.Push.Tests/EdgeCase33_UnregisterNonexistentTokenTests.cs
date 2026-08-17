// Feature: push-updates, EDGE_CASE 3.3: отмена несуществующего токена.
//
// account.unregisterDevice for a token that has no registered device is a no-op: the
// PushDeviceAggregate emits no event and does not throw — this is the path on which the
// handler returns boolTrue (Req 3.3). A fresh aggregate (keyed by token via
// PushDeviceId.Create(token, 12345L)) has never seen a PushDeviceRegisteredEvent, so unregistering
// it must leave state unchanged (no uncommitted events).
//
// Validates: Requirements 3.3

using EventFlow.Aggregates;
using MyTelegram.Domain.Aggregates.PushDevice;
using Shouldly;
using Xunit;

namespace MyTelegram.Push.Tests;

public class EdgeCase33_UnregisterNonexistentTokenTests
{
    [Fact]
    public void Unregister_nonexistent_token_is_noop_and_does_not_throw()
    {
        // Arrange: a fresh aggregate for a token that was never registered.
        const string token = "never-registered-token";
        var aggregate = new PushDeviceAggregate(PushDeviceId.Create(token, 12345L));
        var requestInfo = RequestInfo.Empty with
        {
            UserId = 12345L,
            PermAuthKeyId = 67890L,
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act + Assert: unregistering must not throw (returns boolTrue at the handler level).
        Should.NotThrow(() => aggregate.UnRegisterDevice(
            requestInfo,
            tokenType: 2,
            token,
            otherUids: new long[] { 111L, 222L }));

        // The no-op leaves state unchanged: no event is emitted.
        aggregate.UncommittedEvents.ShouldBeEmpty();
    }
}
