using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Todo;

/// <summary>
/// Covers the domain representation of checklist completions and, in particular, the rule that
/// editing a checklist preserves the completion history of the items that survive the edit.
/// See https://corefork.telegram.org/api/todo
/// </summary>
public class TodoCompletionsTests
{
    [Fact]
    public void ShouldRoundTripCompletions()
    {
        var completions = new List<TodoCompletionItem>
        {
            new(1, new Peer(PeerType.User, 2010001), 1700000000),
            new(2, new Peer(PeerType.Channel, 1500001), 1700000001)
        };

        var media = TodoMediaFactory.Create(CreateList(("First", 1), ("Second", 2)), completions);
        var roundTripped = TodoMediaFactory.ToCompletionItems(media.Completions);

        roundTripped.ShouldBe(completions);
    }

    [Fact]
    public void ShouldAttributeCompletionToAChannelPeer()
    {
        // Anonymous group admins have their completions attributed to the group itself.
        var media = TodoMediaFactory.Create(
            CreateList(("First", 1)),
            [new TodoCompletionItem(1, new Peer(PeerType.Channel, 1500001), 1700000000)]);

        var completion = media.Completions!.Single().ShouldBeOfType<TTodoCompletion>();
        completion.CompletedBy.ShouldBeOfType<TPeerChannel>().ChannelId.ShouldBe(1500001);
    }

    [Fact]
    public void ShouldKeepCompletionsOfItemsSurvivingAnEdit()
    {
        var oldMedia = TodoMediaFactory.Create(
            CreateList(("First", 1), ("Second", 2), ("Third", 3)),
            [
                new TodoCompletionItem(1, new Peer(PeerType.User, 2010001), 1700000000),
                new TodoCompletionItem(2, new Peer(PeerType.User, 2010002), 1700000001)
            ]);

        // The editor drops item 2 and keeps 1 and 3.
        var editedList = CreateList(("First", 1), ("Third", 3));

        var kept = KeepCompletionsForEdit(oldMedia, editedList);

        kept.Select(p => p.Id).ShouldBe([1]);
        kept.Single().CompletedBy.ShouldBe(new Peer(PeerType.User, 2010001));
    }

    [Fact]
    public void ShouldDropAllCompletionsWhenEveryItemIsReplaced()
    {
        var oldMedia = TodoMediaFactory.Create(
            CreateList(("First", 1)),
            [new TodoCompletionItem(1, new Peer(PeerType.User, 2010001), 1700000000)]);

        var kept = KeepCompletionsForEdit(oldMedia, CreateList(("Brand new", 42)));

        kept.ShouldBeEmpty();
    }

    /// <summary>
    /// Mirrors the filtering EditMessageHandler applies when <c>inputMediaTodo</c> replaces the list.
    /// </summary>
    private static List<TodoCompletionItem> KeepCompletionsForEdit(TMessageMediaToDo oldMedia, ITodoList newList)
    {
        var remainingIds = newList.List.Select(p => p.Id).ToHashSet();

        return TodoMediaFactory.ToCompletionItems(oldMedia.Completions)
            .Where(p => remainingIds.Contains(p.Id))
            .ToList();
    }

    private static TTodoList CreateList(params (string Title, int Id)[] items)
    {
        return new TTodoList
        {
            Title = new TTextWithEntities { Text = "Groceries", Entities = new TVector<IMessageEntity>() },
            List = new TVector<ITodoItem>(items.Select(p => new TTodoItem
            {
                Id = p.Id,
                Title = new TTextWithEntities { Text = p.Title, Entities = new TVector<IMessageEntity>() }
            }))
        };
    }
}
