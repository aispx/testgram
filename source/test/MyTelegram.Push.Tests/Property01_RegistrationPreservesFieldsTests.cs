// Feature: push-updates, Property 1: Registration preserves every device field without distortion.
// For any valid registration request (supported TokenType, non-empty Token, arbitrary Secret,
// NoMuted, AppSandbox, OtherUids, UserId, PermAuthKeyId), after applying PushDeviceRegisteredEvent
// the aggregate state AND the read model hold every field identically to the inputs, and the
// registration succeeds (exactly one PushDeviceRegisteredEvent is emitted — the path on which
// RegisterDeviceHandler returns boolTrue).
//
// Validates: Requirements 1.1, 2.1, 2.2, 2.3

using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property01_RegistrationPreservesFieldsTests
{
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Registration_preserves_all_device_fields(DeviceRegistration reg)
    {
        // Arrange: drive the real aggregate exactly as RegisterDeviceHandler does. The aggregate is
        // keyed by token, so a fresh aggregate is unregistered and registration always emits.
        var aggregate = new PushDeviceAggregate(PushDeviceId.Create(reg.Token, reg.UserId));
        var requestInfo = RequestInfo.Empty with
        {
            UserId = reg.UserId,
            PermAuthKeyId = reg.PermAuthKeyId,
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Act
        aggregate.RegisterDevice(
            requestInfo,
            reg.UserId,
            reg.PermAuthKeyId,
            reg.TokenType,
            reg.Token,
            reg.NoMuted,
            reg.AppSandbox,
            reg.Secret,
            reg.OtherUids);

        // A valid registration succeeds: exactly one PushDeviceRegisteredEvent is emitted. This is the
        // path on which the handler returns boolTrue (Req 1.1).
        var uncommitted = aggregate.UncommittedEvents.ToList();
        uncommitted.Count.ShouldBe(1);
        var registeredEvent = uncommitted.Single().AggregateEvent.ShouldBeOfType<PushDeviceRegisteredEvent>();

        // The emitted event preserves every input field without distortion.
        AssertFieldsPreserved(reg,
            registeredEvent.UserId,
            registeredEvent.PermAuthKeyId,
            registeredEvent.TokenType,
            registeredEvent.Token,
            registeredEvent.Secret,
            registeredEvent.NoMuted,
            registeredEvent.AppSandbox,
            registeredEvent.OtherUids);

        // Applying the event to a fresh aggregate state preserves every field (Req 1.1, 2.1, 2.2, 2.3).
        var state = new PushDeviceState();
        state.Apply(registeredEvent);
        state.IsRegistered.ShouldBeTrue();
        AssertFieldsPreserved(reg,
            state.UserId,
            state.PermAuthKeyId,
            state.TokenType,
            state.Token,
            state.Secret,
            state.NoMuted,
            state.AppSandbox,
            state.OtherUids);

        // Applying the event to the read model preserves every field identically to the inputs — this
        // is the projection the delivery pipeline reads back (Req 1.1, 2.1, 2.2, 2.3).
        var readModel = new MyTelegram.ReadModel.Impl.PushDeviceReadModel();
        var domainEvent = new DomainEvent<PushDeviceAggregate, PushDeviceId, PushDeviceRegisteredEvent>(
            registeredEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            PushDeviceId.Create(reg.Token, reg.UserId),
            1);
        readModel.ApplyAsync(null!, domainEvent, CancellationToken.None).GetAwaiter().GetResult();

        readModel.Id.ShouldBe(PushDeviceId.Create(reg.Token, reg.UserId).Value);
        AssertFieldsPreserved(reg,
            readModel.UserId,
            readModel.PermAuthKeyId,
            readModel.TokenType,
            readModel.Token,
            readModel.Secret,
            readModel.NoMuted,
            readModel.AppSandbox,
            readModel.OtherUids);
    }

    private static void AssertFieldsPreserved(
        DeviceRegistration reg,
        long userId,
        long permAuthKeyId,
        int tokenType,
        string? token,
        byte[]? secret,
        bool noMuted,
        bool appSandbox,
        IReadOnlyList<long>? otherUids)
    {
        userId.ShouldBe(reg.UserId);
        permAuthKeyId.ShouldBe(reg.PermAuthKeyId);
        tokenType.ShouldBe(reg.TokenType);
        token.ShouldBe(reg.Token);
        noMuted.ShouldBe(reg.NoMuted);
        appSandbox.ShouldBe(reg.AppSandbox);

        // Secret is stored byte-for-byte (length and content unchanged) — Req 2.1.
        (secret ?? Array.Empty<byte>()).ShouldBe(reg.Secret);

        // OtherUids preserved value-for-value, in order — Req 2.3.
        (otherUids ?? Array.Empty<long>()).ShouldBe(reg.OtherUids);
    }
}
