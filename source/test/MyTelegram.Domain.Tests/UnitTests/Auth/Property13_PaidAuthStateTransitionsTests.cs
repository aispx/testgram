// Feature: auth-methods-completion, Property 13: checkPaidAuth reflects payment completion
//
// For any App_Code that a code delivery has marked payment-required for a pending form id,
// payment completion sets next-step readiness (PaidAuthCompleted = true, PaidAuthRequired = false)
// exactly when the completing form id matches the pending form id; when the completing form id
// mismatches, or no completion occurs at all, the App_Code stays payment-required
// (PaidAuthCompleted = false, PaidAuthRequired = true) with its pending form id intact.
//
// Validates: Requirements 7.1, 7.3
//
// Approach: this property targets the DOMAIN paid-auth infrastructure directly
// (AppCodeAggregate + AppCodeState), NOT the CheckPaidAuthHandler (whose body is deferred to
// task 10). For each generated case a fresh AppCodeAggregate is constructed and driven into the
// "created" state by applying an AppCodeCreatedEvent (so the AggregateIsCreated precondition on
// RequirePaidAuth / CompletePaidAuth holds). The public aggregate methods are then called
// directly: RequirePaidAuth(requestInfo, formId) marks payment required for a pending form; then,
// depending on the generated scenario, either CompletePaidAuth is skipped entirely (the
// "never completed" case), called with the SAME form id (payment completed for the pending form
// -> next-step ready), or called with a DIFFERENT form id (mismatch -> still payment-required).
// Because RequirePaidAuth / CompletePaidAuth mutate through Emit (which applies the event
// immediately via AppCodeState.Apply), the resulting state can be read straight back. The private
// _state is read via reflection (mirroring Property06) and its public getters
// PaidAuthRequired / PaidAuthFormId / PaidAuthCompleted are asserted against the documented
// transitions.

using System.Reflection;
using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.AppCode;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property13_PaidAuthStateTransitionsTests
{
    // Property 13: checkPaidAuth reflects payment completion
    // Validates: Requirements 7.1, 7.3
    [Property(Arbitrary = new[] { typeof(PaidAuthArbitraries) }, MaxTest = 100)]
    public void Paid_auth_state_reflects_payment_completion(PaidAuthCase testCase)
    {
        // Arrange: a fresh App_Code aggregate driven into the "created" state (so the
        // AggregateIsCreated precondition on RequirePaidAuth / CompletePaidAuth holds).
        var now = DateTime.UtcNow.ToTimestamp();
        var aggregateId = AppCodeId.Create(testCase.PhoneNumber, testCase.PhoneCodeHash);

        var createdEvent = new AppCodeCreatedEvent(
            RequestInfo.Empty,
            userId: 0,
            testCase.PhoneNumber,
            code: "12345",
            expire: now + 600,
            testCase.PhoneCodeHash,
            creationTime: now);

        var aggregate = new AppCodeAggregate(aggregateId);
        aggregate.ApplyEvents(new IDomainEvent[]
        {
            new DomainEvent<AppCodeAggregate, AppCodeId, AppCodeCreatedEvent>(
                createdEvent, Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 1)
        });

        // Act: a code delivery requires payment for the pending form id.
        aggregate.RequirePaidAuth(RequestInfo.Empty, testCase.PendingFormId);

        // Depending on the scenario, payment either never completes, completes for the pending
        // form id (match), or completes for a different form id (mismatch).
        bool expectNextStepReady;
        switch (testCase.Scenario)
        {
            case CompletionScenario.NeverCompleted:
                // No CompletePaidAuth call: the App_Code stays payment-required.
                expectNextStepReady = false;
                break;
            case CompletionScenario.MatchingFormId:
                aggregate.CompletePaidAuth(RequestInfo.Empty, testCase.PendingFormId);
                expectNextStepReady = true;
                break;
            case CompletionScenario.MismatchedFormId:
                aggregate.CompletePaidAuth(RequestInfo.Empty, testCase.CompletingFormId);
                expectNextStepReady = false;
                break;
            default:
                throw new InvalidOperationException("Unknown scenario");
        }

        // Assert: read the private _state and verify the documented paid-auth transitions.
        var state = GetState(aggregate);

        // The pending payment form id is always recorded when payment is required
        // (Requirement 7.1). A mismatched or absent completion leaves it untouched; a matching
        // completion clears the requirement but keeps the same recorded form id.
        state.PaidAuthFormId.ShouldBe(testCase.PendingFormId);

        if (expectNextStepReady)
        {
            // Payment completed for the pending form id -> next-step readiness (Requirement 7.3):
            // completed and no longer payment-required.
            state.PaidAuthCompleted.ShouldBeTrue();
            state.PaidAuthRequired.ShouldBeFalse();
        }
        else
        {
            // Incomplete or mismatched form id -> still payment-required (Requirement 7.1):
            // not completed and still flagged as requiring payment.
            state.PaidAuthCompleted.ShouldBeFalse();
            state.PaidAuthRequired.ShouldBeTrue();
        }
    }

    /// <summary>Reads the aggregate's private AppCodeState so the paid-auth flags can be inspected
    /// (the state exposes PaidAuthRequired / PaidAuthFormId / PaidAuthCompleted as public
    /// getters).</summary>
    private static AppCodeState GetState(AppCodeAggregate aggregate)
    {
        var field = typeof(AppCodeAggregate).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (AppCodeState)field.GetValue(aggregate)!;
    }
}

/// <summary>The completion scenario exercised by a Property 13 case: no completion at all, a
/// completion whose form id matches the pending form id, or a completion whose form id
/// differs.</summary>
public enum CompletionScenario
{
    NeverCompleted,
    MatchingFormId,
    MismatchedFormId
}

/// <summary>Input case for Property 13: the phone number and phone code hash that identify the
/// App_Code, the pending payment form id marked by RequirePaidAuth, the completing form id used in
/// the mismatch scenario (guaranteed different from the pending one), and which completion scenario
/// to exercise.</summary>
public sealed record PaidAuthCase(
    string PhoneNumber,
    string PhoneCodeHash,
    long PendingFormId,
    long CompletingFormId,
    CompletionScenario Scenario);

/// <summary>FsCheck arbitrary surface for Property 13. Generates numeric phone numbers, non-empty
/// phone code hashes, a positive pending form id, and one of the three completion scenarios. For
/// the mismatch scenario the completing form id is generated to be strictly different from the
/// pending form id so the mismatch path is genuinely exercised.</summary>
public static class PaidAuthArbitraries
{
    public static Arbitrary<PaidAuthCase> PaidAuthCase()
    {
        var phoneGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var formIdGen = Gen.Choose(1, int.MaxValue).Select(i => (long)i);
        var scenarioGen = Gen.Elements(
            CompletionScenario.NeverCompleted,
            CompletionScenario.MatchingFormId,
            CompletionScenario.MismatchedFormId);

        var gen =
            from phone in phoneGen
            from hash in hashGen
            from pendingForm in formIdGen
            // A completing form id that is guaranteed different from the pending one (for the
            // mismatch scenario); unused by the other scenarios.
            from delta in Gen.Choose(1, int.MaxValue).Select(i => (long)i)
            from scenario in scenarioGen
            select new PaidAuthCase(phone, hash, pendingForm, pendingForm + delta, scenario);

        return Arb.From(gen);
    }
}
