using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Services.Impl;

/// <inheritdoc cref="IPinRightsChecker" />
public class PinRightsChecker(
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IQueryProcessor queryProcessor) : IPinRightsChecker, ITransientDependency
{
    public async Task CheckPinRightsAsync(IRequestInput input, Peer peer)
    {
        if (peer.PeerType != PeerType.Channel)
        {
            return;
        }

        var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
        if (channelReadModel == null)
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
        }

        // Pinning writes to the chat, so unlike a read it is never allowed to a non-member — not even
        // in a public channel anyone can preview.
        if (!await channelAppService.IsChannelMemberAsync(input.UserId, peer.PeerId))
        {
            RpcErrors.RpcErrors400.ChannelPrivate.ThrowRpcError();
        }

        if (channelReadModel!.Broadcast)
        {
            // In a broadcast channel only admins with edit_messages may pin, whatever the default
            // banned rights say — those only ever apply to groups.
            await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId,
                rights => rights.EditMessages,
                RpcErrors.RpcErrors400.ChatAdminRequired);
            return;
        }

        if (await channelAdminRightsChecker.HasChatAdminRightAsync(peer.PeerId, input.UserId,
                rights => rights.PinMessages))
        {
            return;
        }

        // A plain member may pin only when the group grants pin_messages to everyone: otherwise
        // pinning is reserved for admins.
        var defaultBannedRights = channelReadModel.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights();
        if (defaultBannedRights.PinMessages)
        {
            RpcErrors.RpcErrors400.ChatAdminRequired.ThrowRpcError();
        }

        // Even in a group where everyone may pin, a member carrying a still-valid personal restriction
        // may not — the chat defaults and the per-member restriction stack.
        var channelMemberReadModel =
            await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(peer.PeerId, input.UserId));
        var memberBannedRights =
            BannedRightsHelper.GetEffectiveBannedRights(channelMemberReadModel, DateTime.UtcNow.ToTimestamp());

        if (memberBannedRights?.PinMessages == true)
        {
            RpcErrors.RpcErrors400.PinRestricted.ThrowRpcError();
        }
    }
}
