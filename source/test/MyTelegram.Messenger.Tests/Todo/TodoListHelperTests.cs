using MyTelegram.Messenger.Helpers;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Todo;

/// <summary>
/// Covers the limits and item-id rules for
/// <a href="https://corefork.telegram.org/api/todo">todo lists »</a>.
/// </summary>
public class TodoListHelperTests
{
    [Fact]
    public void ShouldAcceptAValidList()
    {
        var list = CreateList(("Buy milk", 1), ("Walk the dog", 2));

        Should.NotThrow(() => TodoListHelper.ValidateTodoList(list));
    }

    [Fact]
    public void ShouldRejectAnEmptyList()
    {
        var list = CreateList();

        ErrorOf(() => TodoListHelper.ValidateTodoList(list)).ShouldBe("TODO_ITEMS_EMPTY");
    }

    [Fact]
    public void ShouldRejectDuplicateItemIds()
    {
        var list = CreateList(("First", 7), ("Second", 7));

        ErrorOf(() => TodoListHelper.ValidateTodoList(list)).ShouldBe("TODO_ITEM_DUPLICATE");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectNonPositiveItemIds(int id)
    {
        var list = CreateList(("First", id));

        // A non-positive id is invalid, not a duplicate: completions are keyed on the item id.
        ErrorOf(() => TodoListHelper.ValidateTodoList(list)).ShouldBe("TODO_ITEM_INVALID");
    }

    [Fact]
    public void ShouldRejectTooManyItems()
    {
        var items = Enumerable.Range(1, TodoListHelper.ItemsMax + 1)
            .Select(i => ($"Item {i}", i))
            .ToArray();

        ErrorOf(() => TodoListHelper.ValidateTodoList(CreateList(items))).ShouldBe("TODO_ITEMS_TOO_MUCH");
    }

    [Fact]
    public void ShouldAcceptExactlyTheMaximumNumberOfItems()
    {
        var items = Enumerable.Range(1, TodoListHelper.ItemsMax)
            .Select(i => ($"Item {i}", i))
            .ToArray();

        Should.NotThrow(() => TodoListHelper.ValidateTodoList(CreateList(items)));
    }

    [Fact]
    public void ShouldRejectATooLongTitle()
    {
        var list = CreateList(("First", 1));
        list.Title = new TTextWithEntities
        {
            Text = new string('a', TodoListHelper.TitleLengthMax + 1),
            Entities = new TVector<IMessageEntity>()
        };

        ErrorOf(() => TodoListHelper.ValidateTodoList(list)).ShouldBe("MESSAGE_TOO_LONG");
    }

    [Fact]
    public void ShouldRejectATooLongItem()
    {
        var list = CreateList((new string('a', TodoListHelper.ItemLengthMax + 1), 1));

        ErrorOf(() => TodoListHelper.ValidateTodoList(list)).ShouldBe("MESSAGE_TOO_LONG");
    }

    [Fact]
    public void ShouldCountEmojiAsASingleCharacter()
    {
        // "😀" is two UTF-16 code units but one character, and TDLib measures these limits in
        // codepoints — counting it as two would reject lists the official client accepts.
        var text = string.Concat(Enumerable.Repeat("😀", TodoListHelper.ItemLengthMax));
        var list = CreateList((text, 1));

        Should.NotThrow(() => TodoListHelper.ValidateTodoList(list));
    }

    [Fact]
    public void ShouldRejectAppendedItemsClashingWithExistingIds()
    {
        var existing = CreateList(("First", 1));
        var appended = new TVector<ITodoItem>(CreateItem("Also first", 1));

        ErrorOf(() => TodoListHelper.ValidateAppendedItems(existing, appended)).ShouldBe("TODO_ITEM_DUPLICATE");
    }

    [Fact]
    public void ShouldRejectAppendedItemsExceedingTheTotalLimit()
    {
        var existing = CreateList(Enumerable.Range(1, TodoListHelper.ItemsMax)
            .Select(i => ($"Item {i}", i))
            .ToArray());
        var appended = new TVector<ITodoItem>(CreateItem("One too many", 9999));

        ErrorOf(() => TodoListHelper.ValidateAppendedItems(existing, appended)).ShouldBe("TODO_ITEMS_TOO_MUCH");
    }

    [Fact]
    public void ShouldAcceptAppendedItemsWithFreshIds()
    {
        var existing = CreateList(("First", 1));
        var appended = new TVector<ITodoItem>(CreateItem("Second", 2));

        Should.NotThrow(() => TodoListHelper.ValidateAppendedItems(existing, appended));
    }

    private static string ErrorOf(Action action)
    {
        return Should.Throw<RpcException>(action).RpcError.Message;
    }

    private static TTodoList CreateList(params (string Title, int Id)[] items)
    {
        return new TTodoList
        {
            Title = new TTextWithEntities { Text = "Groceries", Entities = new TVector<IMessageEntity>() },
            List = new TVector<ITodoItem>(items.Select(p => CreateItem(p.Title, p.Id)))
        };
    }

    private static TTodoItem CreateItem(string title, int id)
    {
        return new TTodoItem
        {
            Id = id,
            Title = new TTextWithEntities { Text = title, Entities = new TVector<IMessageEntity>() }
        };
    }
}
