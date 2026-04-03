using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Fragment;

/// <summary>
/// Fetch information about a fragment collectible (username or phone number purchased on Fragment.com)
/// See https://core.telegram.org/method/fragment.getCollectibleInfo
/// </summary>
internal sealed class GetCollectibleInfoHandler : RpcResultObjectHandler<MyTelegram.Schema.Fragment.RequestGetCollectibleInfo, MyTelegram.Schema.Fragment.ICollectibleInfo>
{
    private readonly IMongoDatabase _database;

    public GetCollectibleInfoHandler(IMongoDatabase database)
    {
        _database = database;
    }

    protected override async Task<MyTelegram.Schema.Fragment.ICollectibleInfo> HandleCoreAsync(
        IRequestInput input,
        MyTelegram.Schema.Fragment.RequestGetCollectibleInfo obj)
    {
        var collection = _database.GetCollection<BsonDocument>("fragment_collectibles");
        BsonDocument? doc = null;

        // Check if it's a username or phone collectible
        if (obj.Collectible is TInputCollectibleUsername usernameInput)
        {
            if (string.IsNullOrWhiteSpace(usernameInput.Username))
            {
                RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
            }

            var filter = Builders<BsonDocument>.Filter.Eq("username", usernameInput.Username.ToLower());
            doc = await collection.Find(filter).FirstOrDefaultAsync();
        }
        else if (obj.Collectible is TInputCollectiblePhone phoneInput)
        {
            if (string.IsNullOrWhiteSpace(phoneInput.Phone))
            {
                RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
            }

            var filter = Builders<BsonDocument>.Filter.Eq("phone", phoneInput.Phone);
            doc = await collection.Find(filter).FirstOrDefaultAsync();
        }
        else
        {
            RpcErrors.RpcErrors400.CollectibleInvalid.ThrowRpcError();
        }

        if (doc == null)
        {
            RpcErrors.RpcErrors400.CollectibleNotFound.ThrowRpcError();
        }

        // Build response with safe type conversion
        return new MyTelegram.Schema.Fragment.TCollectibleInfo
        {
            PurchaseDate = doc["purchase_date"].AsInt32,
            Currency = doc["currency"].AsString,
            Amount = GetInt64(doc["amount"]),
            CryptoCurrency = doc["crypto_currency"].AsString,
            CryptoAmount = GetInt64(doc["crypto_amount"]),
            Url = doc["url"].AsString
        };
    }

    private static long GetInt64(BsonValue value)
    {
        return value.BsonType switch
        {
            BsonType.Int64 => value.AsInt64,
            BsonType.Int32 => value.AsInt32,
            BsonType.Double => (long)value.AsDouble,
            _ => throw new InvalidCastException($"Cannot convert {value.BsonType} to Int64")
        };
    }
}
