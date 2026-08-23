using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Entities;

/// <summary>
/// Turns an arbitrary entity list into a legally nested one, mirroring tdlib's <c>fix_entities</c>
/// (<c>td/telegram/MessageEntity.cpp</c>). The rules are:
/// <list type="bullet">
/// <item>code and pre cannot contain nested entities;</item>
/// <item>code and pre cannot be nested inside anything but a blockquote;</item>
/// <item>continuous and blockquote entities cannot sit inside a continuous entity;</item>
/// <item>blockquotes cannot be nested inside each other;</item>
/// <item>splittable entities crossing a container boundary are cut at that boundary.</item>
/// </list>
/// Anything that still violates a rule is removed rather than reinterpreted: a client that sent it
/// has a broken parser, and a wrong entity renders worse than a missing one.
/// </summary>
internal static class MessageEntityNormalizer
{
    /// <summary>
    /// Upper bound on the entities the split loop may produce. Splitting terminates on its own —
    /// every split consumes a container boundary — but a bound keeps a pathological input from
    /// turning into a long loop.
    /// </summary>
    private const int MaxWorkItems = MessageEntityValidator.MaxEntities * 8;

    public static List<IMessageEntity> Normalize(string? text, IEnumerable<IMessageEntity>? entities)
    {
        var result = new List<IMessageEntity>();
        if (entities == null)
        {
            return result;
        }

        var textLength = text?.Length ?? 0;
        var pending = new List<IMessageEntity>();
        foreach (var entity in entities)
        {
            if (MessageEntityKinds.GetKind(entity) == MessageEntityKind.Dropped)
            {
                continue;
            }

            if (entity.Offset < 0 || entity.Length <= 0 || entity.Offset > textLength - entity.Length)
            {
                continue;
            }

            pending.Add(entity);
        }

        pending.Sort(Compare);

        // Open containers, innermost last.
        var open = new List<IMessageEntity>();
        var index = 0;
        while (index < pending.Count && pending.Count <= MaxWorkItems)
        {
            var entity = pending[index];
            index++;

            while (open.Count > 0 && End(open[^1]) <= entity.Offset)
            {
                open.RemoveAt(open.Count - 1);
            }

            if (open.Count > 0)
            {
                var container = open[^1];
                if (End(entity) > End(container))
                {
                    if (MessageEntityKinds.GetKind(entity) == MessageEntityKind.Splittable)
                    {
                        var boundary = End(container);
                        pending.Insert(index,
                            MessageEntityKinds.CloneWithRange(entity, entity.Offset, boundary - entity.Offset));
                        InsertSorted(pending, index + 1,
                            MessageEntityKinds.CloneWithRange(entity, boundary, End(entity) - boundary));
                    }

                    // The crossing entity itself is never kept, split or not.
                    continue;
                }

                if (!IsNestingAllowed(open, entity))
                {
                    continue;
                }
            }

            result.Add(entity);
            open.Add(entity);
        }

        RemoveDuplicates(result);
        result.Sort(Compare);

        return result;
    }

    private static bool IsNestingAllowed(List<IMessageEntity> open, IMessageEntity entity)
    {
        var kind = MessageEntityKinds.GetKind(entity);
        var hasPre = false;
        var hasContinuous = false;
        var hasBlockquote = false;
        var hasNonBlockquote = false;

        foreach (var container in open)
        {
            if (container.ConstructorId == entity.ConstructorId)
            {
                // bold inside bold, blockquote inside blockquote and friends carry no extra meaning.
                return false;
            }

            switch (MessageEntityKinds.GetKind(container))
            {
                case MessageEntityKind.Pre:
                    hasPre = true;
                    hasNonBlockquote = true;
                    break;
                case MessageEntityKind.Continuous:
                    hasContinuous = true;
                    hasNonBlockquote = true;
                    break;
                case MessageEntityKind.Blockquote:
                    hasBlockquote = true;
                    break;
                default:
                    hasNonBlockquote = true;
                    break;
            }
        }

        if (hasPre)
        {
            return false;
        }

        if (kind == MessageEntityKind.Pre && hasNonBlockquote)
        {
            return false;
        }

        if ((kind == MessageEntityKind.Continuous || kind == MessageEntityKind.Blockquote) && hasContinuous)
        {
            return false;
        }

        if (kind == MessageEntityKind.Blockquote && hasBlockquote)
        {
            return false;
        }

        return true;
    }

    private static void RemoveDuplicates(List<IMessageEntity> entities)
    {
        for (var i = entities.Count - 1; i > 0; i--)
        {
            for (var j = 0; j < i; j++)
            {
                if (MessageEntityKinds.AreEquivalent(entities[j], entities[i]))
                {
                    entities.RemoveAt(i);
                    break;
                }
            }
        }
    }

    private static void InsertSorted(List<IMessageEntity> entities, int start, IMessageEntity entity)
    {
        var position = entities.Count;
        for (var i = start; i < entities.Count; i++)
        {
            if (Compare(entities[i], entity) > 0)
            {
                position = i;
                break;
            }
        }

        entities.Insert(position, entity);
    }

    private static int Compare(IMessageEntity left, IMessageEntity right)
    {
        if (left.Offset != right.Offset)
        {
            return left.Offset.CompareTo(right.Offset);
        }

        // Longer entities open first so that the shorter ones land inside them.
        if (left.Length != right.Length)
        {
            return right.Length.CompareTo(left.Length);
        }

        return MessageEntityKinds.GetPriority(left).CompareTo(MessageEntityKinds.GetPriority(right));
    }

    private static int End(IMessageEntity entity) => entity.Offset + entity.Length;
}
