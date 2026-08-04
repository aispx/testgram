// ReSharper disable once CheckNamespace

namespace MyTelegram;

/// <summary>
/// Builds the TL media object for a <a href="https://corefork.telegram.org/api/todo">todo list »</a>
/// from the domain representation (<see cref="TodoCompletionItem"/>) and back.
/// Shared by the message aggregate state, the read model and the request handlers so that the
/// domain-to-TL mapping lives in exactly one place.
/// </summary>
/// <remarks>
/// The <c>Peer</c> ⇄ <c>IPeer</c> conversion is duplicated here on purpose: the canonical
/// <c>ToPeer()</c> extensions live in <c>MyTelegram.Services</c>, which this project cannot
/// reference (it sits below it in the dependency graph).
/// </remarks>
public static class TodoMediaFactory
{
    public static TMessageMediaToDo Create(ITodoList todo, IReadOnlyCollection<TodoCompletionItem> completions)
    {
        return new TMessageMediaToDo
        {
            Todo = todo,
            Completions = new TVector<ITodoCompletion>(completions.Select(ToTodoCompletion))
        };
    }

    public static ITodoCompletion ToTodoCompletion(TodoCompletionItem completion)
    {
        return new TTodoCompletion
        {
            Id = completion.Id,
            CompletedBy = ToSchemaPeer(completion.CompletedBy),
            Date = completion.Date
        };
    }

    public static List<TodoCompletionItem> ToCompletionItems(TVector<ITodoCompletion>? completions)
    {
        if (completions == null)
        {
            return [];
        }

        var items = new List<TodoCompletionItem>(completions.Count);
        foreach (var completion in completions)
        {
            if (completion is not TTodoCompletion { CompletedBy: not null } todoCompletion)
            {
                continue;
            }

            var completedBy = ToDomainPeer(todoCompletion.CompletedBy);
            if (completedBy == null)
            {
                continue;
            }

            items.Add(new TodoCompletionItem(todoCompletion.Id, completedBy, todoCompletion.Date));
        }

        return items;
    }

    private static IPeer ToSchemaPeer(Peer peer)
    {
        return peer.PeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = peer.PeerId },
            PeerType.Chat => new TPeerChat { ChatId = peer.PeerId },
            _ => new TPeerUser { UserId = peer.PeerId }
        };
    }

    private static Peer? ToDomainPeer(IPeer peer)
    {
        return peer switch
        {
            TPeerChannel peerChannel => new Peer(PeerType.Channel, peerChannel.ChannelId),
            TPeerChat peerChat => new Peer(PeerType.Chat, peerChat.ChatId),
            TPeerUser peerUser => new Peer(PeerType.User, peerUser.UserId),
            _ => null
        };
    }
}
