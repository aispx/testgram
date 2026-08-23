using System.Globalization;

namespace MyTelegram.Messenger.Helpers;

/// <summary>
/// Validation for <a href="https://corefork.telegram.org/api/todo">todo lists »</a>, shared by
/// <c>messages.sendMedia</c>, <c>messages.editMessage</c> and <c>messages.appendTodoList</c> so that
/// the limits are enforced identically everywhere.
/// </summary>
internal static class TodoListHelper
{
    /// <summary>
    /// Limits reported to clients as <c>todo_title_length_max</c>, <c>todo_item_length_max</c> and
    /// <c>todo_items_max</c>. These MUST stay in sync with the values served by
    /// <c>AppConfigHelper.g.cs</c> (keys <c>todo_title_length_max</c> / <c>todo_item_length_max</c> /
    /// <c>todo_items_max</c>); that file is generated, so the constants are mirrored here rather than
    /// parsed back out of the generated JSON.
    /// </summary>
    public const int TitleLengthMax = 255;

    /// <inheritdoc cref="TitleLengthMax"/>
    public const int ItemLengthMax = 200;

    /// <inheritdoc cref="TitleLengthMax"/>
    public const int ItemsMax = 30;

    /// <summary>
    /// Validates a whole checklist as passed to <c>inputMediaTodo</c>.
    /// </summary>
    public static void ValidateTodoList(ITodoList? todoList)
    {
        if (todoList?.Title?.Text == null || todoList.List == null)
        {
            RpcErrors.RpcErrors400.TodoItemsEmpty.ThrowRpcError();
            return;
        }

        if (GetLength(todoList.Title.Text) > TitleLengthMax)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        NormalizeEntities(todoList.Title);

        if (todoList.List.Count == 0)
        {
            RpcErrors.RpcErrors400.TodoItemsEmpty.ThrowRpcError();
        }

        if (todoList.List.Count > ItemsMax)
        {
            TodoExtraRpcErrors.TodoItemsTooMuch.ThrowRpcError();
        }

        var ids = new HashSet<int>();
        foreach (var item in todoList.List)
        {
            ValidateItem(item);

            if (!ids.Add(item.Id))
            {
                RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
            }
        }
    }

    /// <summary>
    /// Validates items appended to an existing checklist via <c>messages.appendTodoList</c>.
    /// </summary>
    public static void ValidateAppendedItems(ITodoList existingList, ICollection<ITodoItem> newItems)
    {
        if (existingList.List.Count + newItems.Count > ItemsMax)
        {
            TodoExtraRpcErrors.TodoItemsTooMuch.ThrowRpcError();
        }

        var ids = existingList.List.Select(x => x.Id).ToHashSet();
        foreach (var item in newItems)
        {
            ValidateItem(item);

            if (!ids.Add(item.Id))
            {
                RpcErrors.RpcErrors400.TodoItemDuplicate.ThrowRpcError();
            }
        }
    }

    private static void ValidateItem(ITodoItem? item)
    {
        if (item?.Title?.Text == null)
        {
            TodoExtraRpcErrors.TodoItemInvalid.ThrowRpcError();
            return;
        }

        // Item ids identify completions, so they must be positive — see TDLib ToDoItem.cpp
        // ("Checklist task identifier must be positive").
        if (item.Id <= 0)
        {
            TodoExtraRpcErrors.TodoItemInvalid.ThrowRpcError();
        }

        if (GetLength(item.Title.Text) > ItemLengthMax)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        NormalizeEntities(item.Title);
    }

    /// <summary>
    /// Checklist titles carry styled text too, so their entities have to be validated and legally
    /// nested like any other text. The server does not autolink them.
    /// See https://corefork.telegram.org/api/entities
    /// </summary>
    private static void NormalizeEntities(ITextWithEntities text)
    {
        MessageEntityValidator.Validate(text.Text, text.Entities);
        text.Entities = new TVector<IMessageEntity>(MessageEntityNormalizer.Normalize(text.Text, text.Entities));
    }

    /// <summary>
    /// Length in text elements rather than UTF-16 code units, so that emoji and other astral
    /// characters count as one — matching TDLib, which measures these limits with <c>utf8_length</c>.
    /// </summary>
    private static int GetLength(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var length = 0;
        while (enumerator.MoveNext())
        {
            length++;
        }

        return length;
    }
}
