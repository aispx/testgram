using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Represents a list of <a href="https://corefork.telegram.org/api/emoji-categories">emoji categories</a>, to be used when selecting custom emojis to set as <a href="https://corefork.telegram.org/api/files#sticker-profile-pictures">profile picture</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiProfilePhotoGroups"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetEmojiProfilePhotoGroupsHandler(IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiProfilePhotoGroups, MyTelegram.Schema.Messages.IEmojiGroups>
{
    protected override async Task<MyTelegram.Schema.Messages.IEmojiGroups> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiProfilePhotoGroups obj)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("emoji_groups");
        var docs = await collection.Find(Builders<BsonDocument>.Filter.Eq("For", "profile_photo"))
            .Sort(Builders<BsonDocument>.Sort.Ascending("Order").Ascending("Title"))
            .ToListAsync();
        var hash = docs.Count == 0 ? 0 : docs.Max(GetVersion);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TEmojiGroupsNotModified();
        }

        return new TEmojiGroups
        {
            Hash = hash,
            Groups = new TVector<IEmojiGroup>(docs.Select(BuildGroup).ToList())
        };
    }

    private static int GetVersion(BsonDocument doc) => doc.Contains("Version") ? doc["Version"].ToInt32() : 0;

    private static IEmojiGroup BuildGroup(BsonDocument doc)
    {
        var kind = doc.Contains("Kind") ? doc["Kind"].AsString : "default";
        var title = doc.Contains("Title") ? doc["Title"].AsString : string.Empty;
        var iconEmojiId = doc.Contains("IconEmojiId") ? doc["IconEmojiId"].ToInt64() : 0;
        var emoticons = new TVector<string>(doc.Contains("Emoticons") && doc["Emoticons"].IsBsonArray
            ? doc["Emoticons"].AsBsonArray.Select(x => x.AsString).ToList()
            : []);

        return kind switch
        {
            "premium" => new TEmojiGroupPremium { Title = title, IconEmojiId = iconEmojiId },
            "greeting" => new TEmojiGroupGreeting { Title = title, IconEmojiId = iconEmojiId, Emoticons = emoticons },
            _ => new TEmojiGroup { Title = title, IconEmojiId = iconEmojiId, Emoticons = emoticons }
        };
    }
}