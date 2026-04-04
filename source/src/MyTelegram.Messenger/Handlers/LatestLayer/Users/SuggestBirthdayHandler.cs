using MyTelegram.Messenger.Services;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Suggest a birthday to a user
/// Possible errors
/// Code Type Description
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.suggestBirthday"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SuggestBirthdayHandler(
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IAccessHashHelper accessHashHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestSuggestBirthday, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestSuggestBirthday obj)
    {
        // Validate access hash
        await accessHashHelper.CheckAccessHashAsync(input, obj.Id);

        // Get target user
        var targetPeer = peerHelper.GetPeer(obj.Id, input.UserId);
        var targetUserId = targetPeer.PeerId;

        // Verify user exists
        var userReadModel = await userAppService.GetAsync(targetUserId);
        if (userReadModel == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Create messageActionSuggestBirthday
        var action = new TMessageActionSuggestBirthday
        {
            Birthday = obj.Birthday
        };

        var toPeer = new Peer(PeerType.User, targetUserId);

        // Send service message to target user
        var sendInput = new SendMessageInput(
            input.ToRequestInfo() with { ReqMsgId = 0 },
            input.UserId,
            toPeer,
            string.Empty,
            Random.Shared.NextInt64(),
            sendMessageType: SendMessageType.MessageService,
            messageType: MessageType.Text,
            messageAction: action
        );

        await messageAppService.SendMessageAsync([sendInput]);

        return new MyTelegram.Schema.TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
    }
}