using FsCheck;
using FsCheck.Xunit;
using MyTelegram;
using MyTelegram.Domain.Aggregates.EncryptedChat;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.EncryptedChat;

/// <summary>
/// Feature: secret-chats, Property 5: State-machine safety and state invariant.
///
/// For any sequence of commands over a secret chat, a state transition happens if and only if it is in
/// the allowed set {Waiting->Discarded, Waiting->Active(accept), Active->Discarded}; Discarded is
/// terminal; an invalid command is rejected without changing the state; and after creation ChatState is
/// always exactly one of {Waiting, Active, Discarded} (Requested is a converter-computed view, never
/// stored, so the stored value never becomes None once created).
///
/// Validates: Requirements 4.5, 4.6, 5.5, 15.1, 15.2, 15.3, 15.4, 15.7.
///
/// The model applies random command sequences to the real aggregate and compares the resulting stored
/// state against an independent reference state machine. Each run executes at least 100 generated cases.
/// </summary>
public class Property05_SecretChatStateMachineTests
{
    private const long AdminId = 1001;
    private const long ParticipantId = 2002;

    public enum Command
    {
        Accept,
        DiscardByAdmin,
        DiscardByParticipant,
        AcceptByAdmin
    }

    [Property(Arbitrary = new[] { typeof(StateMachineArbitraries) }, MaxTest = 200)]
    public void Transitions_happen_iff_allowed_and_Discarded_is_terminal(Command[] commands)
    {
        var aggregate = EncryptedChatTestHelper.NewAggregate(chatId: 7);
        aggregate.CreateEncryptedChat(7, AdminId, ParticipantId, adminPermAuthKeyId: 10,
            accessHash: 99, ga: [1, 2, 3], randomId: 42, date: 100);

        var expected = ChatState.Waiting;
        EncryptedChatTestHelper.GetChatState(aggregate).ShouldBe(ChatState.Waiting);

        foreach (var command in commands)
        {
            var (allowed, next) = Evaluate(expected, command);

            var threw = false;
            try
            {
                Apply(aggregate, command);
            }
            catch (RpcException)
            {
                threw = true;
            }

            // A transition occurred iff the command was allowed for the current state.
            threw.ShouldBe(!allowed);
            expected = next;

            var actual = EncryptedChatTestHelper.GetChatState(aggregate);
            actual.ShouldBe(expected);

            // State is always exactly one of the three stored values (never None after creation).
            actual.ShouldBeOneOf(ChatState.Waiting, ChatState.Active, ChatState.Discarded);
        }
    }

    private static (bool Allowed, ChatState Next) Evaluate(ChatState current, Command command)
    {
        switch (command)
        {
            case Command.Accept:
                // Participant accepts a Waiting chat.
                return current == ChatState.Waiting
                    ? (true, ChatState.Active)
                    : (false, current);
            case Command.AcceptByAdmin:
                // The admin can never accept.
                return (false, current);
            case Command.DiscardByAdmin:
            case Command.DiscardByParticipant:
                return current == ChatState.Discarded
                    ? (false, current)
                    : (true, ChatState.Discarded);
            default:
                return (false, current);
        }
    }

    private static void Apply(EncryptedChatAggregate aggregate, Command command)
    {
        switch (command)
        {
            case Command.Accept:
                aggregate.AcceptEncryptedChat(ParticipantId, participantPermAuthKeyId: 20, gb: [4, 5, 6],
                    keyFingerprint: 123, date: 200);
                break;
            case Command.AcceptByAdmin:
                aggregate.AcceptEncryptedChat(AdminId, participantPermAuthKeyId: 20, gb: [4, 5, 6],
                    keyFingerprint: 123, date: 200);
                break;
            case Command.DiscardByAdmin:
                aggregate.DiscardEncryptedChat(AdminId, deleteHistory: false, date: 300);
                break;
            case Command.DiscardByParticipant:
                aggregate.DiscardEncryptedChat(ParticipantId, deleteHistory: true, date: 300);
                break;
        }
    }

    public static class StateMachineArbitraries
    {
        public static Arbitrary<Command[]> Commands()
        {
            var commandGen = Gen.Elements(
                Command.Accept,
                Command.AcceptByAdmin,
                Command.DiscardByAdmin,
                Command.DiscardByParticipant);

            var sequenceGen = Gen.ListOf(commandGen).Select(list => list.ToArray());

            return Arb.From(sequenceGen);
        }
    }
}
