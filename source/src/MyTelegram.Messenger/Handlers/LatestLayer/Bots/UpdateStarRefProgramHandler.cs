using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;
/// <summary>
/// Create, edit or delete the <a href="https://corefork.telegram.org/api/bots/referrals">affiliate program</a> of a bot we own
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 STARREF_AWAITING_END The previous referral program was terminated less than 24 hours ago: further changes can be made after the date specified in userFull.starref_program.end_date.
/// 400 STARREF_PERMILLE_INVALID The specified commission_permille is invalid: the minimum and maximum values for this parameter are contained in the <a href="https://corefork.telegram.org/api/config#starref-min-commission-permille">starref_min_commission_permille</a> and <a href="https://corefork.telegram.org/api/config#starref-max-commission-permille">starref_max_commission_permille</a> client configuration parameters.
/// 400 STARREF_PERMILLE_TOO_LOW The specified commission_permille is too low: the minimum and maximum values for this parameter are contained in the <a href="https://corefork.telegram.org/api/config#starref-min-commission-permille">starref_min_commission_permille</a> and <a href="https://corefork.telegram.org/api/config#starref-max-commission-permille">starref_max_commission_permille</a> client configuration parameters.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.updateStarRefProgram"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateStarRefProgramHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestUpdateStarRefProgram, MyTelegram.Schema.IStarRefProgram>
{
    protected override async Task<MyTelegram.Schema.IStarRefProgram> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Bots.RequestUpdateStarRefProgram obj)
    {
        if (obj.Bot is not TInputUser)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        var inputUser = (TInputUser)obj.Bot;

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputUser.UserId));
        if (botReadModel == null || !botReadModel.Bot)
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();

        const int minCommissionPermille = 10;
        const int maxCommissionPermille = 500;

        if (obj.CommissionPermille < minCommissionPermille)
            RpcErrors.RpcErrors400.StarrefPermilleTooLow.ThrowRpcError();

        if (obj.CommissionPermille < minCommissionPermille || obj.CommissionPermille > maxCommissionPermille)
            RpcErrors.RpcErrors400.StarrefPermilleInvalid.ThrowRpcError();

        var collection = mongoDatabase.GetCollection<BsonDocument>("bot_star_ref_programs");
        var filter = Builders<BsonDocument>.Filter.Eq("BotId", inputUser.UserId);

        var existingDoc = await collection.Find(filter).FirstOrDefaultAsync();

        if (existingDoc != null && existingDoc.Contains("EndDate") && !existingDoc["EndDate"].IsBsonNull)
        {
            var endDate = existingDoc["EndDate"].ToUniversalTime();
            if ((DateTime.UtcNow - endDate).TotalHours < 24)
                RpcErrors.RpcErrors400.StarrefAwaitingEnd.ThrowRpcError();
        }

        var now = DateTime.UtcNow;
        var durationMonths = obj.DurationMonths ?? 0;

        var update = Builders<BsonDocument>.Update
            .Set("BotId", inputUser.UserId)
            .Set("CommissionPermille", obj.CommissionPermille)
            .Set("DurationMonths", durationMonths)
            .Set("StartDate", now)
            .Set("EndDate", durationMonths > 0 ? (BsonValue)now.AddMonths(durationMonths) : BsonNull.Value)
            .Set("UpdatedAt", now);

        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

        return new TStarRefProgram
        {
            CommissionPermille = obj.CommissionPermille,
            DurationMonths = durationMonths
        };
    }
}