using MyTelegram.ReadModel.Interfaces;

namespace MyTelegram.Converters.Services.Interfaces;

public interface ISessionLocationResolver
{
    SessionLocation Resolve(IDeviceReadModel deviceReadModel);
}

public sealed record SessionLocation(string Country, string Region);
