// Feature: push-updates, Property 29: Conditional inclusion of report_delivery_until_date.
//
// For any new-message notification, the field custom.report_delivery_until_date is present in the
// resulting payload (object model AND serialized JSON) if and only if the originating message has a
// non-empty report_delivery_until_date — i.e. a value that is non-null AND not 0. This exercises the
// production builder (MessagePushDataBuilder.BuildForPersonalMessageAsync /
// BuildForChannelMessageAsync, which set custom.ReportDeliveryUntilDate only when
// item.ReportDeliveryUntilDate is non-null and not 0) and the production serializer
// (PushPayloadEncryptor.BuildJson, which emits the report_delivery_until_date JSON field only when
// the custom value is present). Inputs reuse the task-1 MessageCase generator (new-message kinds
// across User/Chat/Channel peers) with report_delivery_until_date drawn from {null, 0, positive}.
//
// Validates: Requirements 8.4

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.QueryServer.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property29_ReportDeliveryUntilDateTests
{
    /// <summary>The JSON field name official clients read for messages.reportMessagesDelivery scheduling.</summary>
    private const string JsonField = "report_delivery_until_date";

    // Property 29: Conditional inclusion of report_delivery_until_date
    // Validates: Requirements 8.4
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Property29Arbitraries) })]
    public void Report_delivery_until_date_present_iff_message_has_nonempty_value(ReportDeliveryCase testCase)
    {
        // Arrange: deterministic user service (display name resolution is irrelevant to this property).
        var builder = new MessagePushDataBuilder(new StubUserAppService());
        var item = testCase.Item;

        // Act: build the new-message push via the peer-appropriate production builder.
        var pushData = item.ToPeer.PeerType == PeerType.User
            ? builder.BuildForPersonalMessageAsync(item).GetAwaiter().GetResult()
            : builder.BuildForChannelMessageAsync(item, "Chat").GetAwaiter().GetResult();

        // New-message notifications always yield a push.
        pushData.ShouldNotBeNull();
        pushData!.Custom.ShouldNotBeNull();

        // "non-empty" date == non-null and not 0.
        var expectPresent = testCase.ReportValue is { } v && v != 0;

        // Object model: custom.ReportDeliveryUntilDate is set iff the input is non-empty,
        // and when set it carries exactly the input value.
        if (expectPresent)
        {
            pushData.Custom!.ReportDeliveryUntilDate.ShouldBe(testCase.ReportValue);
        }
        else
        {
            pushData.Custom!.ReportDeliveryUntilDate.ShouldBeNull();
        }

        // Serialized JSON: the report_delivery_until_date field appears iff the value is present.
        var json = PushPayloadEncryptor.BuildJson(pushData);
        if (expectPresent)
        {
            json.ShouldContain(JsonField);
            json.ShouldContain($"\"{JsonField}\":{testCase.ReportValue}");
        }
        else
        {
            json.ShouldNotContain(JsonField);
        }
    }
}

/// <summary>
/// A new-message fixture whose <c>report_delivery_until_date</c> is null, 0 or a positive value,
/// for Property 29.
/// </summary>
public sealed record ReportDeliveryCase(MessageItem Item, int? ReportValue)
{
    public override string ToString() =>
        $"ReportDelivery(peer={Item.ToPeer.PeerType}, msgId={Item.MessageId}, " +
        $"report={(ReportValue.HasValue ? ReportValue.Value.ToString() : "null")})";
}

/// <summary>
/// FsCheck arbitrary surface for Property 29. Reuses the task-1 <see cref="PushGen.MessageCase"/>
/// generator (restricted to new-message kinds, excluding Call service notifications which are not
/// new-message pushes) and pairs each with a <c>report_delivery_until_date</c> drawn from
/// {null, 0, positive}, so the iff condition is exercised across both branches.
/// </summary>
public static class Property29Arbitraries
{
    public static Arbitrary<ReportDeliveryCase> ReportDeliveryCase() =>
        Arb.From(ReportDeliveryGen);

    private static Gen<ReportDeliveryCase> ReportDeliveryGen =>
        from mc in PushGen.MessageCase.Where(mc => mc.Kind != MessageKind.Call)
        from report in ReportValue
        select new ReportDeliveryCase(mc.Item with { ReportDeliveryUntilDate = report }, report);

    /// <summary>report_delivery_until_date drawn from null / 0 (both "empty") and positive ("non-empty").</summary>
    private static Gen<int?> ReportValue =>
        Gen.OneOf(
            Gen.Constant((int?)null),
            Gen.Constant((int?)0),
            Gen.Choose(1, 2_000_000_000).Select(i => (int?)i));
}

/// <summary>
/// Minimal <see cref="IUserAppService"/> stub: the builder only resolves the sender display name via
/// <see cref="GetAsync(long)"/> (falling back to "Unknown"), which has no bearing on the
/// report_delivery_until_date field asserted by this property.
/// </summary>
file sealed class StubUserAppService : IUserAppService
{
    public Task<IUserReadModel?> GetAsync(long? id) => Task.FromResult<IUserReadModel?>(null);

    public Task<IUserReadModel> GetAsync(long id) => Task.FromResult<IUserReadModel>(null!);

    public Task<IReadOnlyCollection<IUserReadModel>> GetListAsync(IEnumerable<long> ids) =>
        Task.FromResult<IReadOnlyCollection<IUserReadModel>>(Array.Empty<IUserReadModel>());

    public Task CheckAccountPremiumStatusAsync(long userId) => Task.CompletedTask;

    public Task<IUserFullReadModel?> GetUserFullAsync(long userId) =>
        Task.FromResult<IUserFullReadModel?>(null);

    public void InvalidateCache(long userId) { }
}
