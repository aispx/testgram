using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using TBusinessWorkHours = MyTelegram.Schema.TBusinessWorkHours;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

internal sealed class UpdateBusinessWorkHoursHandler : RpcResultObjectHandler<RequestUpdateBusinessWorkHours, IBool>
{
    private readonly IMongoDatabase _database;
    private readonly IUserAppService _userAppService;

    public UpdateBusinessWorkHoursHandler(IMongoDatabase database, IUserAppService userAppService)
    {
        _database = database;
        _userAppService = userAppService;
    }

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestUpdateBusinessWorkHours obj)
    {
        var userId = input.UserId;

        if (obj.BusinessWorkHours != null)
        {
            var workHours = obj.BusinessWorkHours;
            
            if (string.IsNullOrEmpty(workHours.TimezoneId))
            {
                RpcErrors.RpcErrors400.TimezoneInvalid.ThrowRpcError();
            }

            if (workHours.WeeklyOpen != null && workHours.WeeklyOpen.Count > 0)
            {
                if (workHours.WeeklyOpen.Count > 28)
                {
                    RpcErrors.RpcErrors400.BusinessWorkHoursPeriodInvalid.ThrowRpcError();
                }

                foreach (var weeklyOpen in workHours.WeeklyOpen)
                {
                    if (weeklyOpen.StartMinute >= weeklyOpen.EndMinute)
                    {
                        RpcErrors.RpcErrors400.BusinessWorkHoursPeriodInvalid.ThrowRpcError();
                    }
                }
            }
            else
            {
                RpcErrors.RpcErrors400.BusinessWorkHoursEmpty.ThrowRpcError();
            }

            var openNow = CalculateOpenNow(workHours);
            
            var businessWorkHours = new TBusinessWorkHours
            {
                Flags = obj.BusinessWorkHours.Flags,
                TimezoneId = workHours.TimezoneId,
                WeeklyOpen = workHours.WeeklyOpen,
                OpenNow = openNow
            };

            var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
            var update = Builders<BsonDocument>.Update.Set("BusinessWorkHours", businessWorkHours.ToBsonDocument());
            await collection.UpdateOneAsync(filter, update);
        }
        else
        {
            var collection = _database.GetCollection<BsonDocument>("eventflow-userreadmodel");
            var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId);
            var update = Builders<BsonDocument>.Update.Unset("BusinessWorkHours");
            await collection.UpdateOneAsync(filter, update);
        }

        _userAppService.InvalidateCache(userId);

        return new TBoolTrue();
    }

    private bool CalculateOpenNow(IBusinessWorkHours workHours)
    {
        if (workHours.WeeklyOpen == null || workHours.WeeklyOpen.Count == 0)
            return false;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(workHours.TimezoneId);
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var dayOfWeek = (int)now.DayOfWeek;
            var currentMinutes = dayOfWeek * 24 * 60 + now.Hour * 60 + now.Minute;

            foreach (var weeklyOpen in workHours.WeeklyOpen)
            {
                var startMinutes = weeklyOpen.StartMinute;
                var endMinutes = weeklyOpen.EndMinute;

                if (startMinutes <= currentMinutes && currentMinutes < endMinutes)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
