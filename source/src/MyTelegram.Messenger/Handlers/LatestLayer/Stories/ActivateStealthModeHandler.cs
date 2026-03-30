using MyTelegram.Schema;
using MyTelegram.Schema.Stories;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Stories;
internal sealed class ActivateStealthModeHandler : RpcResultObjectHandler<RequestActivateStealthMode, IUpdates>
{
    protected override Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestActivateStealthMode obj)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var activeUntil = now + 86400 * 7;
        var cooldownUntil = now + 86400 * 30;
        
        var stealthMode = new TStoriesStealthMode
        {
            ActiveUntilDate = (int)activeUntil,
            CooldownUntilDate = (int)cooldownUntil
        };

        return Task.FromResult<IUpdates>(new TUpdates
        {
            Updates = new TVector<IUpdate>
            {
                new TUpdateStoriesStealthMode { StealthMode = stealthMode }
            },
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>(),
            Date = (int)now
        });
    }
}
