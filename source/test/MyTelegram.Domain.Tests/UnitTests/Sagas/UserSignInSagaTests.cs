using MyTelegram.Domain.Sagas.Events;

namespace MyTelegram.Domain.Tests.UnitTests.Sagas;

public class UserSignInSagaTests : TestsFor<SignInSaga>
{
    private readonly Mock<ISagaContext> _sagaContext;

    public UserSignInSagaTests()
    {
        Fixture.Customize<AppCodeId>(c => c.FromFactory(() => AppCodeId.Create("0", "0")));
        Fixture.Customize<SignInSagaId>(c => c.FromFactory(() => new SignInSagaId($"signinsagaid-{Guid.Empty}")));
        _sagaContext = InjectMock<ISagaContext>();
        //var idGenerator = InjectMock<IIdGenerator>();
        //IdGeneratorFactory.SetDefaultIdGenerator(idGenerator.Object);
    }

    [Fact]
    public async Task SignIn_With_Invalid_PhoneCode_Completes_Without_SignInStarted()
    {
        // An invalid phone code no longer throws from the saga: raising the PHONE_CODE_INVALID RPC
        // error is the responsibility of the sign-in handler/aggregate (AppCodeAggregate.CheckCodeCore),
        // not this asynchronous saga which reacts to an already-persisted event. On an invalid code
        // the saga simply completes and starts no sign-in flow (no SignInStartedSagaEvent emitted).
        var aggregateEvent = new CheckSignInCodeCompletedEvent(A<RequestInfo>(), false, 1);
        var domainEvent =
            ADomainEvent<AppCodeAggregate, AppCodeId, CheckSignInCodeCompletedEvent>(aggregateEvent, A<AppCodeId>(), 1);

        await Sut.HandleAsync(domainEvent, _sagaContext.Object, CancellationToken.None);

        Sut.UncommittedEvents.ShouldNotContain(e => e.AggregateEvent is SignInStartedSagaEvent);
    }

    [Fact]
    public async Task SignIn_With_Correct_PhoneCode_Success()
    {
        var aggregateEvent = new CheckSignInCodeCompletedEvent(A<RequestInfo>(), true, 1);
        var domainEvent =
            ADomainEvent<AppCodeAggregate, AppCodeId, CheckSignInCodeCompletedEvent>(aggregateEvent, A<AppCodeId>(), 1);

        await Sut.HandleAsync(domainEvent, _sagaContext.Object, CancellationToken.None);

        Sut.UncommittedEvents.Single().AggregateEvent.ShouldBeOfType<SignInStartedSagaEvent>();
    }
}