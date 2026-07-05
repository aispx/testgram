// Feature: auth-methods-completion, Property 5: A cancelled code cannot be used to sign in
//
// For any App_Code and for any submitted phone code, once the App_Code has been cancelled a
// subsequent sign-in check on that App_Code raises 400 PHONE_CODE_EXPIRED.
//
// Validates: Requirements 2.2
//
// Approach: this property targets the domain aggregate directly (AppCodeAggregate, whose private
// CheckCodeCore is reached through the public CheckSignInCode entry point). For each generated
// case a fresh AppCodeAggregate is constructed and driven into the cancelled state by applying an
// AppCodeCreatedEvent (sequence 1) followed by an AppCodeCanceledEvent (sequence 2) -- exactly the
// event AppCodeAggregate.CancelCode emits. The stored code's Expire is placed in the future so the
// ONLY reason a sign-in can fail is the cancellation (isolating Requirement 2.2 from the
// time-expiry path). The generated submitted code is any non-empty phone code (a code a user would
// actually submit), including one that equals the stored code, to demonstrate that cancellation
// short-circuits the code-equality check. The property asserts CheckSignInCode throws an
// RpcException carrying 400 PHONE_CODE_EXPIRED.

using EventFlow.Aggregates;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Domain.Aggregates.AppCode;

namespace MyTelegram.Domain.Tests.UnitTests.Auth;

public class Property05_CancelledCodeSignInRejectionTests
{
    // Property 5: A cancelled code cannot be used to sign in
    // Validates: Requirements 2.2
    [Property(Arbitrary = new[] { typeof(CancelledSignInArbitraries) }, MaxTest = 100)]
    public void Cancelled_code_cannot_be_used_to_sign_in(CancelledSignInCase testCase)
    {
        // Arrange: a fresh App_Code aggregate whose code is non-expired by time, then cancelled by
        // applying the same event AppCodeAggregate.CancelCode emits (AppCodeCanceledEvent).
        var now = DateTime.UtcNow.ToTimestamp();
        var aggregateId = AppCodeId.Create(testCase.PhoneNumber, testCase.PhoneCodeHash);

        var createdEvent = new AppCodeCreatedEvent(
            RequestInfo.Empty,
            userId: 0,
            testCase.PhoneNumber,
            testCase.StoredCode,
            expire: now + testCase.ExpireOffsetSeconds, // future -> not expired by time
            testCase.PhoneCodeHash,
            creationTime: now);
        var canceledEvent = new AppCodeCanceledEvent(RequestInfo.Empty, testCase.PhoneNumber, testCase.PhoneCodeHash);

        var aggregate = new AppCodeAggregate(aggregateId);
        aggregate.ApplyEvents(new IDomainEvent[]
        {
            new DomainEvent<AppCodeAggregate, AppCodeId, AppCodeCreatedEvent>(
                createdEvent, Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 1),
            new DomainEvent<AppCodeAggregate, AppCodeId, AppCodeCanceledEvent>(
                canceledEvent, Metadata.Empty, DateTimeOffset.UtcNow, aggregateId, 2)
        });

        // Act + Assert: a subsequent sign-in check with ANY submitted phone code raises
        // 400 PHONE_CODE_EXPIRED because the App_Code is cancelled (Requirement 2.2).
        var ex = Should.Throw<RpcException>(() =>
            aggregate.CheckSignInCode(RequestInfo.Empty, testCase.SubmittedCode, userId: 0));

        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("PHONE_CODE_EXPIRED");
    }
}

/// <summary>Input case for Property 5: the phone number and phone code hash that identify the
/// App_Code, the code stored on the (cancelled) App_Code, the non-empty phone code the user submits
/// at sign-in, and a positive expiry offset that keeps the code non-expired by time (so the only
/// cause of rejection is cancellation).</summary>
public sealed record CancelledSignInCase(
    string PhoneNumber,
    string PhoneCodeHash,
    string StoredCode,
    string SubmittedCode,
    int ExpireOffsetSeconds);

/// <summary>FsCheck arbitrary surface for Property 5. Generates numeric phone numbers, non-empty
/// phone code hashes, a stored code, a non-empty submitted code (sometimes equal to the stored code
/// to exercise the case where only cancellation -- not a code mismatch -- causes the rejection),
/// and a positive expiry offset so the App_Code is never expired by time.</summary>
public static class CancelledSignInArbitraries
{
    public static Arbitrary<CancelledSignInCase> CancelledSignInCase()
    {
        var phoneGen = Gen.Choose(10_000_000, 2_000_000_000).Select(i => i.ToString());
        var hashGen = Gen.Choose(1, int.MaxValue).Select(i => "hash" + i);
        var storedCodeGen = Gen.Choose(10_000, 99_999).Select(i => i.ToString());
        var expireGen = Gen.Choose(60, 1_000_000);

        var gen =
            from phone in phoneGen
            from hash in hashGen
            from storedCode in storedCodeGen
            // Submitted code is any non-empty code, including one that matches the stored code.
            from submitted in Gen.OneOf(
                Gen.Constant(storedCode),
                Gen.Choose(10_000, 99_999).Select(i => i.ToString()))
            from expire in expireGen
            select new CancelledSignInCase(phone, hash, storedCode, submitted, expire);

        return Arb.From(gen);
    }
}
