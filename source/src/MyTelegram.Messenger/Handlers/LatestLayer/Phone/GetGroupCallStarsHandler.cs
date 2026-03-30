using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class GetGroupCallStarsHandler
    : RpcResultObjectHandler<RequestGetGroupCallStars, IGroupCallStars>
{
    protected override Task<IGroupCallStars> HandleCoreAsync(IRequestInput input, RequestGetGroupCallStars obj)
    {
        return Task.FromResult<IGroupCallStars>(new TGroupCallStars
        {
            TotalStars = 0,
            TopDonors = new TVector<IGroupCallDonor>(),
            Chats = new TVector<IChat>(),
            Users = new TVector<IUser>()
        });
    }
}