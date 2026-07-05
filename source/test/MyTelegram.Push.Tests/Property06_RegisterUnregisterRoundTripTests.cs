// Feature: push-updates, Property 6: Round-trip регистрация/отмена удаляет устройство.
//
// For any previously registered device, calling account.unregisterDevice with its Token removes the
// device from PushDeviceReadModel and returns boolTrue.
//
// The aggregate is keyed by token (PushDeviceId.Create(token)). After a registration the aggregate
// state is IsRegistered, so PushDeviceAggregate.UnRegisterDevice emits exactly one
// PushDeviceUnRegisteredEvent (the path on which account.unregisterDevice returns boolTrue), and the
// read model projects that event by deleting itself (context.MarkForDeletion()).
//
// Validates: Requirements 3.1

using EventFlow.Aggregates;
using EventFlow.ReadStores;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.PushDevice;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property06_RegisterUnregisterRoundTripTests
{
    // Property 6: Round-trip регистрация/отмена удаляет устройство
    // Validates: Requirements 3.1
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Unregister_of_registered_device_removes_it(DeviceRegistration reg)
    {
        // Arrange: register the device exactly as RegisterDeviceHandler does. Emit applies the event
        // to the aggregate state, so the aggregate is now IsRegistered for this token.
        var aggregate = new PushDeviceAggregate(PushDeviceId.Create(reg.Token));
        var requestInfo = RequestInfo.Empty with
        {
            UserId = reg.UserId,
            PermAuthKeyId = reg.PermAuthKeyId,
            Date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

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

        var eventCountAfterRegister = aggregate.UncommittedEvents.Count();
        eventCountAfterRegister.ShouldBe(1);

        // Project the registration into the read model — this is the device that must be removed.
        var readModel = new MyTelegram.ReadModel.Impl.PushDeviceReadModel();
        var registeredEvent = aggregate.UncommittedEvents
            .Select(e => e.AggregateEvent)
            .OfType<PushDeviceRegisteredEvent>()
            .Single();
        var registeredDomainEvent = new DomainEvent<PushDeviceAggregate, PushDeviceId, PushDeviceRegisteredEvent>(
            registeredEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            PushDeviceId.Create(reg.Token),
            1);
        readModel.ApplyAsync(null!, registeredDomainEvent, CancellationToken.None).GetAwaiter().GetResult();
        readModel.Token.ShouldBe(reg.Token);

        // Act: unregister with the device Token, exactly as account.unregisterDevice does. The owner
        // account plus OtherUids are passed (Req 3.2) — for this round-trip we use the registered set.
        var otherUids = reg.OtherUids?.ToList() ?? new List<long>();
        aggregate.UnRegisterDevice(requestInfo, reg.TokenType, reg.Token, otherUids);

        // Assert: a previously registered device emits exactly one PushDeviceUnRegisteredEvent — the
        // success path on which the handler returns boolTrue (Req 3.1).
        var unregisterEvents = aggregate.UncommittedEvents
            .Skip(eventCountAfterRegister)
            .Select(e => e.AggregateEvent)
            .ToList();
        unregisterEvents.Count.ShouldBe(1);
        var unregisteredEvent = unregisterEvents.Single().ShouldBeOfType<PushDeviceUnRegisteredEvent>();
        unregisteredEvent.Token.ShouldBe(reg.Token);

        // The aggregate state reflects removal.
        // Projecting the unregister event onto the read model deletes the device
        // (context.MarkForDeletion()), i.e. the device is removed from PushDeviceReadModel (Req 3.1).
        var context = new ReadModelContext(null!, PushDeviceId.Create(reg.Token).Value, false);
        var unregisteredDomainEvent = new DomainEvent<PushDeviceAggregate, PushDeviceId, PushDeviceUnRegisteredEvent>(
            unregisteredEvent,
            Metadata.Empty,
            DateTimeOffset.UtcNow,
            PushDeviceId.Create(reg.Token),
            2);
        readModel.ApplyAsync(context, unregisteredDomainEvent, CancellationToken.None).GetAwaiter().GetResult();

        context.IsMarkedForDeletion.ShouldBeTrue();
    }
}
