using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessLocation = MyTelegram.Schema.TBusinessLocation;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdateBusinessLocationHandler : RpcResultObjectHandler<RequestUpdateBusinessLocation, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IUserAppService _userAppService;

    public UpdateBusinessLocationHandler(IMongoDatabase database, IUserAppService userAppService)
    {
        _database = database;
        _userAppService = userAppService;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessLocation obj)
    {
        var userId = input.UserId;
        var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);

        if (obj.Flags.IsBitSet(0) && obj.Address != null)
        {
            IGeoPoint? geoPoint = null;
            if (obj.GeoPoint is TInputGeoPoint inputGeoPoint)
            {
                geoPoint = new TGeoPoint
                {
                    Lat = inputGeoPoint.Lat,
                    Long = inputGeoPoint.Long,
                    AccessHash = 0
                };
            }

            var businessLocation = new TBusinessLocation
            {
                Flags = obj.Flags,
                GeoPoint = geoPoint,
                Address = obj.Address
            };

            var update = Builders<BsonDocument>.Update.Set("BusinessLocation", businessLocation.ToBsonDocument());
            await collection.UpdateOneAsync(filter, update);
        }
        else
        {
            var update = Builders<BsonDocument>.Update.Unset("BusinessLocation");
            await collection.UpdateOneAsync(filter, update);
        }

        _userAppService.InvalidateCache(userId);

        return new TBoolTrue();
    }
}
