// Feature: push-updates, Property 13: Incoming call notification.
//
// For any incoming call, MessagePushDataBuilder.BuildPhoneCall sets loc_key = PHONE_CALL_REQUEST and
// fills custom.call_id, custom.call_ah and custom.updates (the base64url TL-serialization of the
// Updates object carrying updatePhoneCall). This test drives the real builder over generated call
// identifiers (task-1 PushGen.PositiveId) and an opaque byte[] updates blob, then asserts the loc_key,
// the verbatim call id / access hash, and that custom.updates decodes back to the original blob via
// the independent base64url reference codec.
//
// Validates: Requirements 4.5

using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property13_PhoneCallNotificationTests
{
    /// <summary>
    /// An incoming-call fixture: a recipient account id, a call id and access hash (reused task-1
    /// positive-id generator) and an opaque TL-serialized Updates blob of arbitrary length.
    /// </summary>
    private static Gen<(long RecipientUserId, long CallId, long CallAh, byte[] UpdatesTl)> IncomingCall =>
        from recipientUserId in PushGen.PooledUserId
        from callId in PushGen.PositiveId
        from callAh in PushGen.PositiveId
        from length in Gen.Choose(0, 64)
        from updatesTl in GenHelpers.ArrayOfLength(length, Gen.Choose(0, 255).Select(i => (byte)i))
        select (recipientUserId, callId, callAh, updatesTl);

    // Property 13: Incoming call notification
    // Validates: Requirements 4.5
    [Property(MaxTest = 100)]
    public Property Phone_call_push_sets_lockey_and_call_fields()
    {
        // BuildPhoneCall does not consult the user app service, so a null dependency is sufficient.
        var builder = new MessagePushDataBuilder(userAppService: null!);

        return Prop.ForAll(Arb.From(IncomingCall), call =>
        {
            var (recipientUserId, callId, callAh, updatesTl) = call;

            PushData data = builder.BuildPhoneCall(recipientUserId, callId, callAh, updatesTl);

            data.LocKey.ShouldBe(PushNotificationTypes.PhoneCallRequest);
            data.UserId.ShouldBe(recipientUserId);

            data.Custom.ShouldNotBeNull();
            data.Custom!.CallId.ShouldBe(callId);
            data.Custom.CallAh.ShouldBe(callAh);

            data.Custom.Updates.ShouldNotBeNull();
            Base64UrlReference.Decode(data.Custom.Updates!).ShouldBe(updatesTl);

            return true;
        });
    }
}
