using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Look for <a href="https://corefork.telegram.org/api/custom-emoji">custom emojis</a> associated to a UTF8 emoji
/// Possible errors
/// Code Type Description
/// 400 EMOTICON_EMPTY The emoji is empty.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.searchCustomEmoji"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SearchCustomEmojiHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSearchCustomEmoji, MyTelegram.Schema.IEmojiList>
{
    protected override async Task<MyTelegram.Schema.IEmojiList> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSearchCustomEmoji obj)
    {
        var emoticon = obj.Emoticon?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(emoticon))
        {
            RpcErrors.RpcErrors400.EmoticonEmpty.ThrowRpcError();
        }

        var emoticons = await ResolveEmoticonsAsync(emoticon);
        var documentIds = await FindCustomEmojiDocumentIdsAsync(emoticon, emoticons);
        var hash = ComputeHash(documentIds);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TEmojiListNotModified();
        }

        return new TEmojiList
        {
            Hash = hash,
            DocumentId = new TVector<long>(documentIds)
        };
    }

    private async Task<HashSet<string>> ResolveEmoticonsAsync(string emoticon)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { emoticon };
        var keywordCollection = mongoDatabase.GetCollection<BsonDocument>("emoji_keywords");
        // Expand only from an exact keyword match. Looking up every keyword row that
        // merely contains the emoji pulls in broad buckets such as "animated emoji",
        // which then makes 😀 resolve to unrelated documents like ❤.
        var filter = Builders<BsonDocument>.Filter.Eq("Keyword", emoticon);
        var keywordDocs = await keywordCollection.Find(filter).ToListAsync();
        foreach (var doc in keywordDocs)
        {
            if (!doc.Contains("Emoticons") || !doc["Emoticons"].IsBsonArray)
            {
                continue;
            }

            foreach (var value in doc["Emoticons"].AsBsonArray)
            {
                if (value.IsString && !string.IsNullOrWhiteSpace(value.AsString))
                {
                    result.Add(value.AsString);
                }
            }
        }

        return result;
    }

    private async Task<List<long>> FindCustomEmojiDocumentIdsAsync(string originalQuery, HashSet<string> emoticons)
    {
        var setCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-stickersetreadmodel");
        var emojiSetFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("Emojis", true),
            // inputStickerSetAnimatedEmoji is the built-in animated rendering for ordinary
            // Unicode emoji, not a user-selectable custom-emoji set.
            Builders<BsonDocument>.Filter.Ne("ShortName", "AnimatedEmojies"));
        var emojiSets = await setCollection.Find(emojiSetFilter).ToListAsync();
        var candidateDocumentIds = new SortedSet<long>();

        foreach (var setDoc in emojiSets)
        {
            AddPackMatches(setDoc, emoticons, candidateDocumentIds);
            AddKeywordMatches(setDoc, originalQuery, emoticons, candidateDocumentIds);
        }

        return await FilterValidCustomEmojiDocumentsAsync(candidateDocumentIds);
    }

    private async Task<List<long>> FilterValidCustomEmojiDocumentsAsync(IEnumerable<long> candidateDocumentIds)
    {
        var ids = candidateDocumentIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var documentCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-documentreadmodel");
        var filter = Builders<BsonDocument>.Filter.In("DocumentId", ids.Select(x => (BsonValue)new BsonInt64(x)));
        var docs = await documentCollection.Find(filter).ToListAsync();
        var validIds = docs
            .Where(IsValidCustomEmojiDocument)
            .Select(x => GetInt64(x["DocumentId"]))
            .ToHashSet();

        return ids.Where(validIds.Contains).ToList();
    }

    private static void AddPackMatches(BsonDocument setDoc, HashSet<string> emoticons, SortedSet<long> documentIds)
    {
        if (!setDoc.Contains("Packs") || !setDoc["Packs"].IsBsonArray)
        {
            return;
        }

        foreach (var packValue in setDoc["Packs"].AsBsonArray)
        {
            if (!packValue.IsBsonDocument)
            {
                continue;
            }

            var pack = packValue.AsBsonDocument;
            var packEmoticon = pack.Contains("Emoticon") && pack["Emoticon"].IsString
                ? pack["Emoticon"].AsString
                : string.Empty;
            if (!emoticons.Contains(packEmoticon) || !pack.Contains("Documents") || !pack["Documents"].IsBsonArray)
            {
                continue;
            }

            foreach (var value in pack["Documents"].AsBsonArray)
            {
                if (TryGetInt64(value, out var documentId))
                {
                    documentIds.Add(documentId);
                }
            }
        }
    }

    private static void AddKeywordMatches(BsonDocument setDoc, string originalQuery, HashSet<string> emoticons, SortedSet<long> documentIds)
    {
        if (!setDoc.Contains("Keywords") || !setDoc["Keywords"].IsBsonArray)
        {
            return;
        }

        foreach (var keywordValue in setDoc["Keywords"].AsBsonArray)
        {
            if (!keywordValue.IsBsonDocument)
            {
                continue;
            }

            var keywordDoc = keywordValue.AsBsonDocument;
            if (!keywordDoc.Contains("Keyword") || !keywordDoc["Keyword"].IsBsonArray ||
                !keywordDoc.Contains("DocumentId") || !TryGetInt64(keywordDoc["DocumentId"], out var documentId))
            {
                continue;
            }

            var matches = keywordDoc["Keyword"].AsBsonArray
                .Where(x => x.IsString)
                .Select(x => x.AsString)
                .Any(x => string.Equals(x, originalQuery, StringComparison.OrdinalIgnoreCase) || emoticons.Contains(x));
            if (matches)
            {
                documentIds.Add(documentId);
            }
        }
    }

    private static long ComputeHash(IEnumerable<long> documentIds)
    {
        var ids = documentIds.ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        unchecked
        {
            long hash = 1469598103934665603;
            foreach (var documentId in ids)
            {
                hash ^= documentId;
                hash *= 1099511628211;
            }

            return hash;
        }
    }

    private static bool TryGetInt64(BsonValue value, out long result)
    {
        switch (value.BsonType)
        {
            case BsonType.Int64:
                result = value.AsInt64;
                return true;
            case BsonType.Int32:
                result = value.AsInt32;
                return true;
            case BsonType.Double:
                result = (long)value.AsDouble;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool IsValidCustomEmojiDocument(BsonDocument document)
    {
        if (!document.Contains("Attributes2") || document["Attributes2"].IsBsonNull)
        {
            return false;
        }

        return CustomEmojiAttributeHelper.TryGetCustomEmojiAttribute(document, out _);
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
