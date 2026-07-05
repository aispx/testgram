using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;
/// <summary>
/// Obtain information about a <a href="https://corefork.telegram.org/api/giveaways">Telegram Premium giftcode »</a>
/// Possible errors
/// Code Type Description
/// 400 GIFT_SLUG_EXPIRED The specified gift slug has expired.
/// 400 GIFT_SLUG_INVALID The specified slug is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/payments.checkGiftCode"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckGiftCodeHandler(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Payments.RequestCheckGiftCode, MyTelegram.Schema.Payments.ICheckedGiftCode>
{
    protected override async Task<MyTelegram.Schema.Payments.ICheckedGiftCode> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Payments.RequestCheckGiftCode obj)
    {
        var collection = mongoDatabase.GetCollection<BsonDocument>("premium_gift_codes");
        var filter = Builders<BsonDocument>.Filter.Eq("Slug", obj.Slug);
        var code = await collection.Find(filter).FirstOrDefaultAsync();

        if (code == null)
            RpcErrors.RpcErrors400.SlugInvalid.ThrowRpcError();

        // Allow viewing used codes (client will show "Already used" UI)
        // Don't throw error here - ApplyGiftCodeHandler prevents re-activation

        // Load chats and users
        var chats = new TVector<IChat>();
        var users = new TVector<IUser>();

        var fromId = code.Contains("FromId") && !code["FromId"].IsBsonNull ? code["FromId"].AsInt64 : 0L;
        var used = code.Contains("Used") && code["Used"].AsBoolean;
        // Per Telegram spec, to_id must only be present after the code has been imported (activated).
        // For not-yet-activated codes (including giveaway winners who haven't claimed yet) we return null.
        var toId = used && code.Contains("UsedBy") && !code["UsedBy"].IsBsonNull
            ? code["UsedBy"].AsInt64
            : (long?)null;
        var viaGiveaway = code.Contains("ViaGiveaway") && code["ViaGiveaway"].AsBoolean;

        // For giveaways, use ChannelId instead of FromId
        var channelId = viaGiveaway && code.Contains("ChannelId") && !code["ChannelId"].IsBsonNull
            ? code["ChannelId"].AsInt64
            : 0L;

        // Load channel for giveaways
        if (channelId > 0)
        {
            chats.Add(await chatConverterService.GetChannelAsync(input, channelId, false, false, input.Layer));
        }

        // Load to user if ToId is set
        if (toId.HasValue)
        {
            var userReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(toId.Value));
            if (userReadModel != null)
            {
                users.Add(userConverterService.ToUser(input, userReadModel, layer: input.Layer));
            }
        }

        var giveawayMsgId = code.Contains("GiveawayMsgId") && !code["GiveawayMsgId"].IsBsonNull
            ? code["GiveawayMsgId"].AsInt32
            : (int?)null;

        var months = code.Contains("Months") ? code["Months"].AsInt32 : 1;
        var days = months * 30; // Convert months to days
        var date = code.Contains("Date") ? code["Date"].AsInt32 : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var usedDate = code.Contains("UsedDate") && !code["UsedDate"].IsBsonNull
            ? code["UsedDate"].AsInt32
            : (int?)null;

        return new TCheckedGiftCode
        {
            ViaGiveaway = viaGiveaway,
            FromId = channelId > 0 ? new TPeerChannel { ChannelId = channelId } : null,
            GiveawayMsgId = giveawayMsgId,
            ToId = toId,
            Date = date,
            Days = days,
            UsedDate = usedDate,
            Chats = chats,
            Users = users
        };
    }
}
