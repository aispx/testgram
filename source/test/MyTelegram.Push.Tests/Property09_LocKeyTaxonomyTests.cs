// Feature: push-updates, Property 9: loc_key always belongs to a known taxonomy.
//
// For any built PushData, the loc_key field is non-empty and belongs to the set of
// PushNotificationTypes constants. This is exercised by driving the PRODUCTION
// MessagePushDataBuilder over the task-1 MessageCase generator (text/media/reaction/call across
// User/Chat/Channel peers): User peers go through BuildForPersonalMessageAsync, Chat/Channel peers
// through BuildForChannelMessageAsync. The same fixtures also feed every service (cancel) builder
// (BuildMessageDeleted / BuildReadHistory / BuildReadReaction / BuildPhoneCall). Every PushData the
// builder returns must carry a loc_key that is non-empty and is a member of the allowed set built by
// reflecting over the public static string fields of PushNotificationTypes. The builder depends on
// IUserAppService only to resolve a display name, so a minimal stub (returning no user => "Unknown")
// is supplied.
//
// Validates: Requirements 4.1

using System.Reflection;
using System.Threading;
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Services.Services;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property09_LocKeyTaxonomyTests
{
    /// <summary>
    /// The allowed loc_key taxonomy: every value declared as a public static string field on
    /// <see cref="PushNotificationTypes"/> (covers both <c>const</c> and <c>static readonly</c>).
    /// </summary>
    private static readonly HashSet<string> AllowedLocKeys =
        typeof(PushNotificationTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToHashSet(StringComparer.Ordinal);

    // Property 9: loc_key always belongs to a known taxonomy
    // Validates: Requirements 4.1
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Built_loc_key_is_non_empty_and_in_taxonomy(MessageCase messageCase)
    {
        var builder = new MessagePushDataBuilder(new StubUserAppService());
        var item = messageCase.Item;
        var peer = item.ToPeer;

        // Collect every PushData the builder can produce from this fixture: the message-derived push
        // (personal vs channel by peer type) plus all service (cancel) builders driven by the same ids.
        var built = new List<PushData?>
        {
            peer.PeerType == PeerType.User
                ? builder.BuildForPersonalMessageAsync(item).GetAwaiter().GetResult()
                : builder.BuildForChannelMessageAsync(item, "Channel/Group Title").GetAwaiter().GetResult(),
            builder.BuildMessageDeleted(item.OwnerPeer.PeerId, peer, new[] { item.MessageId }),
            builder.BuildReadHistory(item.OwnerPeer.PeerId, peer, item.MessageId),
            builder.BuildReadReaction(item.OwnerPeer.PeerId, peer, new[] { item.MessageId }),
            builder.BuildPhoneCall(item.OwnerPeer.PeerId, item.RandomId, item.RandomId + 1, new byte[] { 1, 2, 3, 4 })
        };

        foreach (var pushData in built)
        {
            // BuildForPersonalMessageAsync intentionally returns null for service/action messages
            // (e.g. an incoming call) — nothing is pushed, so there is no loc_key to validate.
            if (pushData is null)
            {
                continue;
            }

            pushData.LocKey.ShouldNotBeNullOrEmpty();
            AllowedLocKeys.ShouldContain(
                pushData.LocKey,
                $"kind={messageCase.Kind}, peer={peer.PeerType}, loc_key='{pushData.LocKey}'");
        }
    }

    /// <summary>
    /// Minimal <see cref="IUserAppService"/> stub. The builder only calls <c>GetAsync(long)</c> to
    /// resolve a display name and gracefully falls back to "Unknown" when no user is found, so every
    /// member returns an empty/absent result. Members unused by the builder throw if ever called.
    /// </summary>
    private sealed class StubUserAppService : IUserAppService
    {
        public Task<IUserReadModel?> GetAsync(long? id) => Task.FromResult<IUserReadModel?>(null);

        public Task<IUserReadModel> GetAsync(long id) => Task.FromResult<IUserReadModel>(null!);

        public Task<IReadOnlyCollection<IUserReadModel>> GetListAsync(IEnumerable<long> ids) =>
            Task.FromResult<IReadOnlyCollection<IUserReadModel>>(Array.Empty<IUserReadModel>());

        public Task CheckAccountPremiumStatusAsync(long userId) => Task.CompletedTask;

        public Task<IUserFullReadModel?> GetUserFullAsync(long userId) =>
            Task.FromResult<IUserFullReadModel?>(null);

        public void InvalidateCache(long userId)
        {
        }
    }
}
