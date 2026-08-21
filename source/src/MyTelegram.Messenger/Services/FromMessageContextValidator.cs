using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace MyTelegram.Messenger.Services;

/// <summary>
/// Finds every <c>input*FromMessage</c> constructor reachable from a request and hands each one to
/// <see cref="IFromMessagePeerResolver"/>, so the cited context is proven before the handler runs.
/// See https://corefork.telegram.org/api/min
/// </summary>
public sealed class FromMessageContextValidator(
    IFromMessagePeerResolver resolver,
    ILogger<FromMessageContextValidator> logger)
    : IFromMessageContextValidator, ITransientDependency
{
    /// <summary>
    /// Requests nest a handful of levels at most (a media album holds items, an item holds a reply
    /// header, a reply header holds a peer), so this only has to be deep enough to reach a peer
    /// without following a hostile cycle forever.
    /// </summary>
    private const int MaxDepth = 12;

    /// <summary>
    /// Each cited context costs a message lookup. No real client names hundreds of separate min
    /// contexts in one request, so a request that does is refused rather than served.
    /// </summary>
    private const int MaxReferences = 256;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> WalkablePropertyCache = new();

    public async Task ValidateAsync(IRequestInput input, IObject request)
    {
        var references = Collect(request);
        if (references.Count == 0)
        {
            return;
        }

        foreach (var reference in references)
        {
            if (reference.IsChannel)
            {
                await resolver.ResolveChannelIdAsync(input, reference.Container, reference.MsgId, reference.TargetId);
            }
            else
            {
                await resolver.ResolveUserIdAsync(input, reference.Container, reference.MsgId, reference.TargetId);
            }
        }

        logger.LogDebug(
            "Validated {Count} fromMessage context(s) for user {UserId} request {ObjectId:x8}",
            references.Count,
            input.UserId,
            input.ObjectId);
    }

    private static List<FromMessageReference> Collect(IObject request)
    {
        var references = new List<FromMessageReference>();
        var seen = new HashSet<(bool IsChannel, long ContainerKey, int MsgId, long TargetId)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Walk(request, 0, visited, seen, references);

        return references;
    }

    private static void Walk(
        object? value,
        int depth,
        HashSet<object> visited,
        HashSet<(bool, long, int, long)> seen,
        List<FromMessageReference> references)
    {
        if (value is null || depth > MaxDepth)
        {
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string or byte[])
        {
            return;
        }

        // Only reference types can form the cycles this guards against, and boxing a value type
        // produces a fresh object every time, which would make the set grow without ever matching.
        if (!type.IsValueType && !visited.Add(value))
        {
            return;
        }

        switch (value)
        {
            case TInputPeerUserFromMessage peerUser:
                Add(references, seen, new FromMessageReference(false, peerUser.Peer, peerUser.MsgId, peerUser.UserId));
                break;
            case TInputUserFromMessage user:
                Add(references, seen, new FromMessageReference(false, user.Peer, user.MsgId, user.UserId));
                break;
            case TInputPeerChannelFromMessage peerChannel:
                Add(references, seen,
                    new FromMessageReference(true, peerChannel.Peer, peerChannel.MsgId, peerChannel.ChannelId));
                break;
            case TInputChannelFromMessage channel:
                Add(references, seen,
                    new FromMessageReference(true, channel.Peer, channel.MsgId, channel.ChannelId));
                break;
        }

        if (references.Count > MaxReferences)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        // A fromMessage constructor still has its container peer walked below: the container may be
        // a fromMessage constructor of its own, and it needs proving just as much.
        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Walk(item, depth + 1, visited, seen, references);
            }

            return;
        }

        foreach (var property in GetWalkableProperties(type))
        {
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch (Exception)
            {
                // A property that throws cannot hold a peer worth validating.
                continue;
            }

            Walk(child, depth + 1, visited, seen, references);
        }
    }

    private static void Add(
        List<FromMessageReference> references,
        HashSet<(bool, long, int, long)> seen,
        FromMessageReference reference)
    {
        // The same context cited twice in one request only needs proving once.
        if (seen.Add((reference.IsChannel, ContainerKey(reference.Container), reference.MsgId, reference.TargetId)))
        {
            references.Add(reference);
        }
    }

    /// <summary>
    /// A cheap identity for the container peer, used only to skip duplicate work. Peer id ranges do
    /// not overlap, so the bare id separates users from channels on its own.
    /// </summary>
    private static long ContainerKey(IInputPeer? container)
    {
        return container switch
        {
            null => -1,
            TInputPeerUser peerUser => peerUser.UserId,
            TInputPeerChannel peerChannel => peerChannel.ChannelId,
            TInputPeerChat peerChat => peerChat.ChatId,
            TInputPeerUserFromMessage peerUser => peerUser.UserId,
            TInputPeerChannelFromMessage peerChannel => peerChannel.ChannelId,
            TInputPeerSelf => 0,
            _ => -2
        };
    }

    private static PropertyInfo[] GetWalkableProperties(Type type)
    {
        return WalkablePropertyCache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && MayHoldPeer(p.PropertyType))
            .ToArray());
    }

    /// <summary>
    /// Whether a property of this declared type is worth following: another schema object, or a
    /// collection that could hold one. Scalars cannot lead to a peer.
    /// </summary>
    private static bool MayHoldPeer(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(byte[]) ||
            type == typeof(object))
        {
            return false;
        }

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return MayHoldPeer(underlying);
        }

        if (typeof(IObject).IsAssignableFrom(type))
        {
            return true;
        }

        if (!typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        // Vector<int>/Vector<long> are the common case and hold nothing to validate.
        var elementType = type.IsArray
            ? type.GetElementType()
            : type.IsGenericType
                ? type.GetGenericArguments().FirstOrDefault()
                : null;

        return elementType == null || MayHoldPeer(elementType);
    }

    private readonly record struct FromMessageReference(bool IsChannel, IInputPeer? Container, int MsgId, long TargetId);
}
