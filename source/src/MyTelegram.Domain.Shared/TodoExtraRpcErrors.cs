namespace MyTelegram;

/// <summary>
/// Hand-written RPC errors for <a href="https://corefork.telegram.org/api/todo">todo lists »</a>
/// that are not present in the generated <see cref="RpcErrors"/> (<c>RpcErrors.g.cs</c>).
/// Do not add these to the generated file; it is regenerated and would lose manual edits.
/// </summary>
public static class TodoExtraRpcErrors
{
    /// <summary>
    /// A checklist item with a non-positive id was passed; item ids must be positive and unique.
    /// <code>
    /// messages.appendTodoList
    /// messages.editMessage
    /// messages.sendMedia
    /// </code>
    /// </summary>
    public static readonly RpcError TodoItemInvalid = new(400, "TODO_ITEM_INVALID");

    /// <summary>
    /// Too many checklist items: the list exceeds
    /// <a href="https://corefork.telegram.org/api/config#todo-items-max">todo_items_max »</a>.
    /// <code>
    /// messages.appendTodoList
    /// messages.editMessage
    /// messages.sendMedia
    /// </code>
    /// </summary>
    public static readonly RpcError TodoItemsTooMuch = new(400, "TODO_ITEMS_TOO_MUCH");
}
