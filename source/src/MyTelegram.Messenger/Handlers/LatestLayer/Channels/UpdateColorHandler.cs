using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Update the <a href="https://corefork.telegram.org/api/colors">accent color and background custom emoji »</a> of a channel.
/// Possible errors
/// Code Type Description
/// 400 BOOSTS_REQUIRED The specified channel must first be <a href="https://corefork.telegram.org/api/boost">boosted by its users</a> in order to perform this action.
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 COLOR_INVALID The specified color palette ID was invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.updateColor"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateColorHandler(
    ICommandBus commandBus,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IPeerColorPaletteProvider peerColorPaletteProvider,
    IBoostLevelCalculator boostLevelCalculator,
    IQueryProcessor queryProcessor,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestUpdateColor, MyTelegram.Schema.IUpdates>
{
    /// <summary>Matches <c>channel_bg_icon_level_min</c> in the app config.</summary>
    private const int ChannelBgIconLevelMin = 4;

    /// <summary>Matches <c>channel_profile_bg_icon_level_min</c> in the app config.</summary>
    private const int ChannelProfileBgIconLevelMin = 7;

    /// <summary>Matches <c>group_profile_bg_icon_level_min</c> in the app config.</summary>
    private const int GroupProfileBgIconLevelMin = 5;

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestUpdateColor obj)
    {
        var channel = peerHelper.GetChannel(obj.Channel);
        // Repainting a channel is an appearance change, so it needs ChangeInfo -- the same right
        // every sibling appearance handler checks (UpdateEmojiStatus, EditPhoto, EditTitle).
        // PinMessages is an independent flag: an admin promoted with only pin_messages, which
        // deliberately withholds change_info, could otherwise recolor the whole channel.
        await channelAdminRightsChecker.CheckAdminRightAsync(channel.PeerId, input.UserId, p => p.ChangeInfo, RpcErrors.RpcErrors400.ChatAdminRequired);

        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channel.PeerId));
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        var isBroadcast = channelReadModel!.Broadcast;
        int? boostLevel = null;

        if (obj.Color.HasValue)
        {
            var option = peerColorPaletteProvider.GetOption(obj.Color.Value, obj.ForProfile);
            if (option == null)
            {
                RpcErrors.RpcErrors400.ColorInvalid.ThrowRpcError();
            }

            var minLevel = isBroadcast ? option!.ChannelMinLevel : option!.GroupMinLevel;
            if (minLevel is > 0)
            {
                boostLevel ??= await boostLevelCalculator.GetLevelAsync(channel.PeerId);
                if (boostLevel < minLevel.Value)
                {
                    RpcErrors.RpcErrors400.BoostsRequired.ThrowRpcError();
                }
            }
        }

        if (obj.BackgroundEmojiId.HasValue)
        {
            var backgroundEmojiMinLevel = obj.ForProfile
                ? isBroadcast ? ChannelProfileBgIconLevelMin : GroupProfileBgIconLevelMin
                : ChannelBgIconLevelMin;

            boostLevel ??= await boostLevelCalculator.GetLevelAsync(channel.PeerId);
            if (boostLevel < backgroundEmojiMinLevel)
            {
                RpcErrors.RpcErrors400.BoostsRequired.ThrowRpcError();
            }
        }

        var prevColor = obj.ForProfile ? channelReadModel.ProfileColor : channelReadModel.Color;

        var color = new PeerColor(obj.Color, obj.BackgroundEmojiId);
        var command = new UpdateChannelColorCommand(ChannelId.Create(channel.PeerId), input.ToRequestInfo(), color, obj.BackgroundEmojiId, obj.ForProfile);
        await commandBus.PublishAsync(command);

        if (obj.ForProfile)
        {
            await AdminLogHelper.LogChangeProfilePeerColor(mongoDatabase, channel.PeerId, input.UserId, prevColor.ToPeerColor(), color.ToPeerColor());
        }
        else
        {
            await AdminLogHelper.LogChangePeerColor(mongoDatabase, channel.PeerId, input.UserId, prevColor.ToPeerColor(), color.ToPeerColor());
        }

        // Updates are pushed asynchronously by ChannelDomainEventHandler once
        // ChannelColorUpdatedEvent is projected, as with the other channel mutation handlers.
        return null!;
    }
}
