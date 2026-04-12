using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

internal sealed class UpdateStarRefProgramHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestUpdateStarRefProgram, MyTelegram.Schema.IStarRefProgram>
{
    protected override async Task<MyTelegram.Schema.IStarRefProgram> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestUpdateStarRefProgram obj)
    {
        var inputUser = obj.Bot as TInputUser;
        if (inputUser == null)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var botCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var botFilter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("UserId", inputUser.UserId),
            Builders<BsonDocument>.Filter.Eq("CreatorUserId", input.UserId)
        );
        var botDoc = await botCollection.Find(botFilter).FirstOrDefaultAsync();
        if (botDoc == null)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("star_ref_programs");
        var filter = Builders<BsonDocument>.Filter.Eq("bot_id", inputUser.UserId);
        var existingDoc = await collection.Find(filter).FirstOrDefaultAsync();

        // Check if program is awaiting termination (end_date is set but not yet passed)
        if (existingDoc != null && existingDoc.Contains("end_date") && !existingDoc["end_date"].IsBsonNull)
        {
            var endDate = existingDoc["end_date"].AsInt32;
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (endDate > now)
                RpcErrors.RpcErrors400.StarrefAwaitingEnd.ThrowRpcError();
        }

        if (obj.CommissionPermille == 0)
        {
            if (existingDoc == null)
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var endDate = now + (24 * 3600);

            await collection.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("end_date", endDate));

            return new TStarRefProgram
            {
                BotId = inputUser.UserId,
                CommissionPermille = existingDoc["commission_permille"].AsInt32,
                DurationMonths = existingDoc.Contains("duration_months") ? existingDoc["duration_months"].AsInt32 : null,
                EndDate = endDate
            };
        }

        if (obj.CommissionPermille < 10 || obj.CommissionPermille > 500)
            RpcErrors.RpcErrors400.StarrefPermilleInvalid.ThrowRpcError();

        // Validate that commission and duration can only be raised
        if (existingDoc != null)
        {
            var oldCommission = existingDoc["commission_permille"].AsInt32;
            if (obj.CommissionPermille < oldCommission)
                RpcErrors.RpcErrors400.StarrefPermilleTooLow.ThrowRpcError();

            if (obj.DurationMonths.HasValue && existingDoc.Contains("duration_months"))
            {
                var oldDuration = existingDoc["duration_months"].AsInt32;
                if (obj.DurationMonths.Value < oldDuration)
                    RpcErrors.RpcErrors400.StarrefPermilleInvalid.ThrowRpcError();
            }
        }

        var now2 = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var durationMonths = obj.DurationMonths ?? 0;

        await collection.UpdateOneAsync(
            filter,
            Builders<BsonDocument>.Update
                .Set("bot_id", inputUser.UserId)
                .Set("commission_permille", obj.CommissionPermille)
                .Set("duration_months", durationMonths)
                .Set("date", now2)
                .Unset("end_date"),
            new UpdateOptions { IsUpsert = true }
        );

        return new TStarRefProgram
        {
            BotId = inputUser.UserId,
            CommissionPermille = obj.CommissionPermille,
            DurationMonths = durationMonths > 0 ? durationMonths : null
        };
    }
}
