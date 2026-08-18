using MongoDB.Driver;
using MyTelegram.Messenger.Extensions;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Change the set of <a href="https://corefork.telegram.org/api/reactions">message reactions »</a> that
/// can be used in a certain group, supergroup or channel.
/// Possible errors
/// Code Type Description
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 REACTIONS_TOO_MANY Too many reactions were specified.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setChatAvailableReactions"/> </c></para>
/// </summary>
internal sealed class SetChatAvailableReactionsHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IChannelAppService channelAppService,
    IMongoDatabase mongoDatabase,
    IAppConfigHelper appConfigHelper) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetChatAvailableReactions, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetChatAvailableReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        await channelAdminRightsChecker.CheckAdminRightAsync(peer.PeerId, input.UserId, r => r.ChangeInfo,
            RpcErrors.RpcErrors403.ChatAdminRequired);

        var (reactionType, availableReactions, allowCustom) = ParseChatReactions(obj.AvailableReactions,
            appConfigHelper.GetInt32Value("reactions_in_chat_max", 100));

        var prevReactions = await GetCurrentReactionsAsync(peer.PeerId);

        // paid_enabled is optional: when the client omits it, keep whatever the channel had.
        var paidEnabled = obj.PaidEnabled ?? await GetCurrentPaidEnabledAsync(peer.PeerId);

        var command = new SetAvailableReactionsCommand(
            ChannelId.Create(peer.PeerId),
            input.ToRequestInfo(),
            reactionType,
            availableReactions,
            allowCustom,
            paidEnabled);
        await commandBus.PublishAsync(command);

        await AdminLogHelper.LogChangeAvailableReactions(mongoDatabase, peer.PeerId, input.UserId,
            prevReactions, obj.AvailableReactions);

        // The updated channel is pushed by ChannelDomainEventHandler via updateChannel.
        return null!;
    }

    /// <summary>
    /// The reactions currently allowed, rebuilt in the same shape <c>channelFull.available_reactions</c>
    /// reports them (see ChannelFullMapper), so the admin log entry shows the real previous value.
    /// </summary>
    private async Task<IChatReactions> GetCurrentReactionsAsync(long channelId)
    {
        var channelFull = await channelAppService.GetChannelFullAsync(channelId);
        if (channelFull == null)
        {
            return new TChatReactionsNone();
        }

        return channelFull.ReactionType switch
        {
            // ReactionNone means "never configured", which behaves as all reactions allowed.
            ReactionType.ReactionNone => new TChatReactionsAll(),
            ReactionType.ReactionAll => new TChatReactionsAll { AllowCustom = channelFull.AllowCustomReaction },
            ReactionType.ReactionSome when channelFull.AvailableReactions?.Count > 0 => new TChatReactionsSome
            {
                Reactions = new TVector<IReaction>(
                    channelFull.AvailableReactions.Select(p => new TReactionEmoji { Emoticon = p }))
            },
            _ => new TChatReactionsNone()
        };
    }

    private static (ReactionType ReactionType, List<string>? AvailableReactions, bool AllowCustom) ParseChatReactions(
        IChatReactions chatReactions,
        int reactionsInChatMax)
    {
        switch (chatReactions)
        {
            case TChatReactionsAll all:
                return (ReactionType.ReactionAll, null, all.AllowCustom);

            case TChatReactionsSome some:
            {
                if (some.Reactions.Count > reactionsInChatMax)
                {
                    RpcErrors.RpcErrors400.ReactionsTooMany.ThrowRpcError();
                }

                var emoticons = new List<string>();
                foreach (var reaction in some.Reactions)
                {
                    switch (reaction)
                    {
                        case TReactionEmoji emoji when !string.IsNullOrEmpty(emoji.Emoticon):
                            emoticons.Add(emoji.Emoticon);
                            break;
                        // Custom emoji cannot be whitelisted individually: the API models that as
                        // chatReactionsAll.allow_custom, so a custom entry here is invalid.
                        default:
                            RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                            break;
                    }
                }

                return (ReactionType.ReactionSome, emoticons, false);
            }

            // chatReactionsNone is "some, but empty": nothing is allowed.
            case TChatReactionsNone:
                return (ReactionType.ReactionSome, [], false);

            default:
                RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
                return (ReactionType.ReactionNone, null, false);
        }
    }

    private async Task<bool> GetCurrentPaidEnabledAsync(long channelId)
    {
        var channelReadModel = await queryProcessor.ProcessAsync(new GetChannelByIdQuery(channelId));
        return channelReadModel?.PaidReactionsEnabled ?? false;
    }
}
