using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Edit the name of a <a href="https://corefork.telegram.org/api/channel">channel/supergroup</a>
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 403 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_INVALID Invalid chat.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// 400 CHAT_TITLE_EMPTY No chat title provided.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.editTitle"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class EditTitleHandler(
    ICommandBus commandBus,
    IRandomHelper randomHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IAccessHashHelper accessHashHelper,
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestEditTitle, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestEditTitle obj)
    {
        if (obj.Channel is TInputChannel inputChannel)
        {
            await channelAdminRightsChecker.CheckAdminRightAsync(obj.Channel, input.UserId, adminRights => adminRights.ChangeInfo);
            await accessHashHelper.CheckAccessHashAsync(input, inputChannel.ChannelId, inputChannel.AccessHash, AccessHashType.Channel);

            // Get current title for admin log
            var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(inputChannel.ChannelId));
            var prevTitle = channelReadModel?.Title ?? "";

            // Log to admin log
            await AdminLogHelper.LogChangeTitle(mongoDatabase, inputChannel.ChannelId, input.UserId, prevTitle, obj.Title);

            var command = new EditChannelTitleCommand(ChannelId.Create(inputChannel.ChannelId), input.ToRequestInfo(), obj.Title, new TMessageActionChatEditTitle { Title = obj.Title }, randomHelper.NextInt64());
            await commandBus.PublishAsync(command, CancellationToken.None);

            return null !;
        }

        throw new NotImplementedException();
    }
}