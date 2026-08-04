// ReSharper disable once CheckNamespace

namespace MyTelegram;

/// <summary>
/// A single completed item of a <a href="https://corefork.telegram.org/api/todo">todo list »</a>.
/// Stored inside the message aggregate/read model instead of the TL type
/// <c>todoCompletion</c> so that domain events stay free of TL entities
/// (same approach as <see cref="Reaction"/> for message reactions).
/// </summary>
/// <param name="Id">Id of the completed item, matches <c>todoItem.id</c>.</param>
/// <param name="CompletedBy">
/// Peer that completed the item. Normally the user, but for anonymous group admins the
/// channel itself — see https://corefork.telegram.org/api/todo (layer 217+).
/// </param>
/// <param name="Date">Unixtime the item was completed at.</param>
public record TodoCompletionItem(int Id, Peer CompletedBy, int Date);
