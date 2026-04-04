using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;
/// <summary>
/// Adds or removes a song from the current user's profile <a href="https://corefork.telegram.org/api/profile#music">see here »</a> for more info on the music tab of the profile page.
/// Possible errors
/// Code Type Description
/// 400 DOCUMENT_INVALID The specified document is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.saveMusic"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SaveMusicHandler(IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestSaveMusic, IBool>, IObjectHandler
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Account.RequestSaveMusic obj)
    {
        // Validate document
        if (obj.Id is not TInputDocument inputDoc)
        {
            RpcErrors.RpcErrors400.DocumentInvalid.ThrowRpcError();
            return null!;
        }

        var collection = database.GetCollection<BsonDocument>("saved_music");
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
            Builders<BsonDocument>.Filter.Eq("DocumentId", inputDoc.Id)
        );

        if (obj.Unsave)
        {
            // Remove from saved music
            await collection.DeleteOneAsync(filter);
        }
        else
        {
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Handle after_id for ordering
            int order = 0;
            if (obj.AfterId is TInputDocument afterDoc)
            {
                // Find the after_id document
                var afterFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Eq("DocumentId", afterDoc.Id)
                );
                var afterDocResult = await collection.Find(afterFilter).FirstOrDefaultAsync();

                if (afterDocResult == null)
                {
                    // after_id not found in saved music - return false
                    return new TBoolFalse();
                }

                // Get order of after_id document
                order = afterDocResult.Contains("Order") ? afterDocResult["Order"].AsInt32 : 0;

                // Increment orders of all documents after this one
                var updateFilter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("UserId", input.UserId),
                    Builders<BsonDocument>.Filter.Gt("Order", order)
                );
                var incrementUpdate = Builders<BsonDocument>.Update.Inc("Order", 1);
                await collection.UpdateManyAsync(updateFilter, incrementUpdate);

                order++; // Place after the after_id document
            }
            else
            {
                // No after_id - add to top (order 0)
                // Increment all existing orders
                var allFilter = Builders<BsonDocument>.Filter.Eq("UserId", input.UserId);
                var incrementUpdate = Builders<BsonDocument>.Update.Inc("Order", 1);
                await collection.UpdateManyAsync(allFilter, incrementUpdate);
                order = 0;
            }

            // Add or update the document
            var update = Builders<BsonDocument>.Update
                .Set("UserId", input.UserId)
                .Set("DocumentId", inputDoc.Id)
                .Set("Date", now)
                .Set("Order", order);

            await collection.UpdateOneAsync(
                filter,
                update,
                new UpdateOptions { IsUpsert = true }
            );

            // Limit to max 10 saved music items per user
            var allDocs = await collection.Find(
                Builders<BsonDocument>.Filter.Eq("UserId", input.UserId)
            ).Sort(Builders<BsonDocument>.Sort.Ascending("Order")).ToListAsync();

            if (allDocs.Count > 10)
            {
                // Remove items beyond limit
                var toRemove = allDocs.Skip(10).ToList();
                foreach (var doc in toRemove)
                {
                    await collection.DeleteOneAsync(
                        Builders<BsonDocument>.Filter.Eq("_id", doc["_id"])
                    );
                }
            }
        }

        return new TBoolTrue();
    }
}