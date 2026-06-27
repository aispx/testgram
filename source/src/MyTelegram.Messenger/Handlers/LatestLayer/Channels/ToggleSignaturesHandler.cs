using MyTelegram.Messenger.Services.Impl;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Enable/disable message signatures in channels
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleSignatures"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleSignaturesHandler(
    ICommandBus commandBus,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleSignatures, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestToggleSignatures obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Channel, input.UserId);
            await commandBus.PublishAsync(new ToggleSignatureCommand(ChannelId.Create(inputChannel.ChannelId), input.ToRequestInfo(), obj.SignaturesEnabled, obj.ProfilesEnabled));

            // Log to admin log
            await AdminLogHelper.LogToggleSignatures(mongoDatabase, inputChannel.ChannelId, input.UserId, obj.SignaturesEnabled);

            return null !;
        }

        throw new NotImplementedException();
    }
}