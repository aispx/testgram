// Feature: push-updates, Property 12: Уведомление о реакции (reaction notification)
using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using Shouldly;

namespace MyTelegram.Push.Tests;

/// <summary>
/// Property 12: Уведомление о реакции.
///
/// <para>
/// For any reaction event on a user's message, the payload builder
/// (<see cref="MessagePushDataBuilder.BuildReaction"/>) sets <c>loc_key</c> to a value from the
/// <c>REACT_*</c> (1:1) or <c>CHAT_REACT_*</c> (group/channel) family and sets <c>custom.msg_id</c>
/// equal to the id of the message the reaction was placed on.
/// </para>
///
/// Validates: Requirements 4.4
/// </summary>
public class Property12_ReactionNotificationTests
{
    // BuildReaction is a pure function and never touches the IUserAppService dependency, so a null
    // service is sufficient for exercising it directly.
    private static readonly MessagePushDataBuilder Builder = new(null!);

    /// <summary>
    /// The set of loc_keys belonging to the reaction families, discovered from the taxonomy so the
    /// assertion stays in sync with <see cref="PushNotificationTypes"/>.
    /// </summary>
    private static readonly HashSet<string> ReactionFamily = PushGen.AllLocKeys
        .Where(k => k.StartsWith("REACT_", StringComparison.Ordinal)
                    || k.StartsWith("CHAT_REACT_", StringComparison.Ordinal))
        .ToHashSet();

    [Property(MaxTest = 100, Arbitrary = new[] { typeof(PushArbitraries) })]
    public void Reaction_notification_uses_react_family_loc_key_and_carries_reacted_msg_id(MessageCase mc)
    {
        // Property 12 concerns reaction events; the generator tags those as MessageKind.Reaction.
        if (mc.Kind != MessageKind.Reaction)
        {
            return;
        }

        var reactedMessage = mc.Item;
        var recipientUserId = reactedMessage.SenderUserId;
        var reaction = reactedMessage.Reactions?.FirstOrDefault()?.Emoticon ?? "👍";

        var push = Builder.BuildReaction(
            recipientUserId,
            reactedMessage,
            reactorName: "Reactor",
            reaction: reaction,
            chatName: "Group");

        // loc_key is non-empty and belongs to the REACT_*/CHAT_REACT_* family.
        push.LocKey.ShouldNotBeNullOrWhiteSpace();
        ReactionFamily.ShouldContain(push.LocKey);

        // custom.msg_id equals the id of the message the reaction was placed on.
        push.Custom.ShouldNotBeNull();
        push.Custom!.MsgId.ShouldBe(reactedMessage.MessageId);

        // user_id is the recipient (the author of the reacted message).
        push.UserId.ShouldBe(recipientUserId);
    }
}
