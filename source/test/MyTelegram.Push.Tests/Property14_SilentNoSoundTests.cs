// Feature: push-updates, Property 14: silent эквивалентен отсутствию звука.
//
// For any built new-message notification, the silent flag and the audible sound are mutually
// exclusive: if the source MessageItem is marked silent, then the production
// MessagePushDataBuilder sets custom.silent to a truthy value AND leaves Sound unset (null);
// if it is not marked silent, then Sound is set (non-null). This drives the real builder over the
// task-1 MessageCase generator (text/media/reaction fixtures across User/Chat/Channel peers,
// excluding service-only calls): User peers go through BuildForPersonalMessageAsync, Chat/Channel
// peers through BuildForChannelMessageAsync. The Silent flag is varied true/false per case.
//
// Validates: Requirements 4.6

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.Schema;

namespace MyTelegram.Push.Tests;

public class Property14_SilentNoSoundTests
{
    /// <summary>
    /// New-message fixtures (Text/Media/Reaction) whose builders always return a push, reusing the
    /// task-1 <see cref="PushGen.MessageCase"/> generator. Incoming calls are service notifications
    /// (BuildForPersonalMessageAsync returns null and BuildPhoneCall handles sound separately), so
    /// they are excluded here. Each case is paired with a generated <c>Silent</c> flag that is
    /// applied to the <see cref="MessageItem"/>.
    /// </summary>
    private static Gen<(MessageCase Case, bool Silent)> SilentCase =>
        from mc in PushGen.MessageCase.Where(c => c.Kind != MessageKind.Call)
        from silent in Arb.Generate<bool>()
        select (mc with { Item = mc.Item with { Silent = silent } }, silent);

    // Property 14: silent эквивалентен отсутствию звука
    // Validates: Requirements 4.6
    [Property(MaxTest = 100)]
    public Property Silent_message_has_no_sound_and_non_silent_has_sound()
    {
        return Prop.ForAll(Arb.From(SilentCase), testCase =>
        {
            var (mc, silent) = testCase;
            var builder = new MessagePushDataBuilder(new StubUserAppService());
            var item = mc.Item;

            var push = item.ToPeer.PeerType == PeerType.User
                ? builder.BuildForPersonalMessageAsync(item).GetAwaiter().GetResult()
                : builder.BuildForChannelMessageAsync(item, "Channel/Group Title").GetAwaiter().GetResult();

            // These fixtures always produce a push.
            if (push is null)
            {
                return false.Label($"unexpected null push for {mc}");
            }

            var custom = push.Custom!;

            // Silent <=> custom.silent truthy AND Sound == null; otherwise Sound is set.
            var ok = silent
                ? custom.Silent == true && push.Sound is null
                : custom.Silent != true && push.Sound is not null;

            return ok.Label(
                $"{mc}: silent={silent}, custom.Silent={custom.Silent}, " +
                $"Sound={push.Sound ?? "null"}");
        });
    }
}
