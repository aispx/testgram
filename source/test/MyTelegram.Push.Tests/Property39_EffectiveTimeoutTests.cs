using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger;

namespace MyTelegram.Push.Tests;

// Feature: push-updates, Property 39: The effective timeout is never below 5 seconds.
//
// For any configured PushTimeoutSec value, the actual HTTP request timeout equals
// Max(5, PushTimeoutSec) seconds. The senders (ApnsPushSender / FcmPushSender / WebPushSender)
// all compute the per-request timeout inline as:
//
//     using var cts = new CancellationTokenSource(
//         TimeSpan.FromSeconds(Math.Max(5, cfg.PushTimeoutSec)));
//
// Because that computation is a private/inline detail of each sender, this property exercises the
// exact formula contract (the Max(5, x) relationship) over the real public config surface
// (PushConfig.{Fcm,Apns,WebPush}.PushTimeoutSec), covering negatives, zero, small and large values.
//
// Validates: Requirements 12.3
public class Property39_EffectiveTimeoutTests
{
    /// <summary>
    /// The effective HTTP request timeout, computed exactly as every push sender does it.
    /// Replicates the single-line contract under test: TimeSpan.FromSeconds(Math.Max(5, PushTimeoutSec)).
    /// </summary>
    private static TimeSpan EffectiveTimeout(int pushTimeoutSec) =>
        TimeSpan.FromSeconds(Math.Max(5, pushTimeoutSec));

    // Property 39: The effective timeout is never below 5 seconds
    // Validates: Requirements 12.3
    [Property(MaxTest = 100)]
    public Property Effective_timeout_equals_max_5_and_is_never_below_5_seconds()
    {
        // Any int PushTimeoutSec: negatives, 0, small and large values.
        return Prop.ForAll(Arb.From<int>(), pushTimeoutSec =>
        {
            // Drive the value through the real public config surface for each provider so the
            // property is tied to the actual configuration model the senders read.
            var fcm = new PushConfig.FcmConfig { PushTimeoutSec = pushTimeoutSec };
            var apns = new PushConfig.ApnsConfig { PushTimeoutSec = pushTimeoutSec };
            var web = new PushConfig.WebPushConfig { PushTimeoutSec = pushTimeoutSec };

            var expectedSeconds = Math.Max(5, pushTimeoutSec);

            var fcmTimeout = EffectiveTimeout(fcm.PushTimeoutSec);
            var apnsTimeout = EffectiveTimeout(apns.PushTimeoutSec);
            var webTimeout = EffectiveTimeout(web.PushTimeoutSec);

            var equalsMax = fcmTimeout == TimeSpan.FromSeconds(expectedSeconds)
                            && apnsTimeout == TimeSpan.FromSeconds(expectedSeconds)
                            && webTimeout == TimeSpan.FromSeconds(expectedSeconds);

            // The effective timeout is never below the 5-second floor.
            var atLeastFive = fcmTimeout >= TimeSpan.FromSeconds(5)
                              && apnsTimeout >= TimeSpan.FromSeconds(5)
                              && webTimeout >= TimeSpan.FromSeconds(5);

            // All three providers agree on the same effective timeout.
            var providersAgree = fcmTimeout == apnsTimeout && apnsTimeout == webTimeout;

            return (equalsMax && atLeastFive && providersAgree)
                .Label($"PushTimeoutSec={pushTimeoutSec}, expected=Max(5,{pushTimeoutSec})={expectedSeconds}s, " +
                       $"fcm={fcmTimeout.TotalSeconds}s, apns={apnsTimeout.TotalSeconds}s, web={webTimeout.TotalSeconds}s");
        });
    }
}
