using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Bots;

/// <summary>
/// Verify a user or chat on behalf of an organization.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 403 BOT_VERIFIER_FORBIDDEN This bot cannot assign verification icons.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/bots.setCustomVerification"/> </c></para>
/// <para><c>See <a href="https://corefork.telegram.org/api/bots/verification"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class SetCustomVerificationHandler(
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IBotVerificationStore botVerificationStore,
    IBotOwnershipChecker botOwnershipChecker,
    IAccessHashHelper2 accessHashHelper,
    IFromMessagePeerResolver fromMessagePeerResolver,
    IChannelUpdateNotifier channelUpdateNotifier,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Bots.RequestSetCustomVerification, IBool>
{
    /// <summary>
    /// Mirrors <c>bot_verification_description_length_limit</c> in <c>AppConfigHelper</c>, which is
    /// what clients read to pre-validate the field.
    /// See https://corefork.telegram.org/api/config#bot-verification-description-length-limit
    /// </summary>
    private const int DescriptionLengthLimit = 70;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Bots.RequestSetCustomVerification obj)
    {
        var botId = await ResolveVerifierBotIdAsync(input, obj.Bot);

        // The badge is rendered with this organisation's icon and name, so a bot that Telegram (here:
        // the server operator) never authorised must not be able to mint one - not even an empty one.
        var settings = await botVerificationStore.GetVerifierSettingsAsync(botId);
        if (settings == null || settings.Icon == 0)
        {
            RpcErrors.RpcErrors403.BotVerifierForbidden.ThrowRpcError();
        }

        var (targetUserId, targetChannelId) = await ResolveTargetPeerAsync(input, obj.Peer);

        if (!obj.Enabled)
        {
            var removed = await botVerificationStore.RemoveAsync(botId, targetUserId, targetChannelId);
            if (removed)
            {
                await NotifyPeerChangedAsync(input, targetUserId, targetChannelId);
            }

            return new TBoolTrue();
        }

        var document = new BotVerificationDocument
        {
            Id = targetUserId != 0 ? $"verification-user-{targetUserId}" : $"verification-channel-{targetChannelId}",
            BotId = botId,
            Icon = settings!.Icon,
            Company = settings.Company,
            Description = ResolveDescription(settings, obj.CustomDescription),
            UserId = targetUserId,
            ChannelId = targetChannelId
        };

        await botVerificationStore.SetAsync(document);
        await NotifyPeerChangedAsync(input, targetUserId, targetChannelId);

        return new TBoolTrue();
    }

    /// <summary>
    /// <c>bot</c> must not be set when a bot calls this itself, and must be one of the caller's own
    /// bots otherwise. See https://corefork.telegram.org/method/bots.setCustomVerification
    /// </summary>
    private async Task<long> ResolveVerifierBotIdAsync(IRequestInput input, IInputUser? bot)
    {
        if (bot == null || bot is TInputUserSelf)
        {
            var self = await userAppService.GetAsync((long?)input.UserId);
            if (self == null || !self.Bot)
            {
                RpcErrors.RpcErrors400.UserBotInvalid.ThrowRpcError();
            }

            return input.UserId;
        }

        long botId;
        switch (bot)
        {
            case TInputUser inputUser:
                await accessHashHelper.CheckAccessHashAsync(input, inputUser);
                botId = inputUser.UserId;
                break;
            case TInputUserFromMessage fromMessage:
                botId = await fromMessagePeerResolver.ResolveUserIdAsync(input, fromMessage.Peer, fromMessage.MsgId,
                    fromMessage.UserId);
                break;
            default:
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
                return 0;
        }

        var botReadModel = await userAppService.GetAsync((long?)botId);
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        if (botId != input.UserId && !await botOwnershipChecker.IsOwnerAsync(botId, input.UserId))
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        return botId;
    }

    /// <summary>
    /// Only users and channels can carry a badge: <c>chat</c> and <c>chatFull</c> have no
    /// <c>bot_verification</c> field at all, so a legacy group is refused rather than silently
    /// stored under an id that belongs to the channel namespace.
    /// </summary>
    private async Task<(long UserId, long ChannelId)> ResolveTargetPeerAsync(IRequestInput input, IInputPeer? peer)
    {
        long userId = 0;
        long channelId = 0;

        switch (peer)
        {
            case TInputPeerSelf:
                userId = input.UserId;
                break;
            case TInputPeerUser inputPeerUser:
                await accessHashHelper.CheckAccessHashAsync(input, inputPeerUser);
                userId = inputPeerUser.UserId;
                break;
            case TInputPeerUserFromMessage fromMessage:
                userId = await fromMessagePeerResolver.ResolveUserIdAsync(input, fromMessage.Peer, fromMessage.MsgId,
                    fromMessage.UserId);
                break;
            case TInputPeerChannel inputPeerChannel:
                await accessHashHelper.CheckAccessHashAsync(input, inputPeerChannel);
                channelId = inputPeerChannel.ChannelId;
                break;
            case TInputPeerChannelFromMessage fromMessage:
                channelId = await fromMessagePeerResolver.ResolveChannelIdAsync(input, fromMessage.Peer,
                    fromMessage.MsgId, fromMessage.ChannelId);
                break;
            default:
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
                break;
        }

        if (userId != 0)
        {
            var user = await userAppService.GetAsync((long?)userId);
            if (user == null || user.IsDeleted == true)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }
        else
        {
            var channel = await channelAppService.GetAsync((long?)channelId);
            if (channel == null)
            {
                RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            }
        }

        return (userId, channelId);
    }

    /// <summary>
    /// The bot's own description wins when it is allowed to set one, then the organisation's default,
    /// and finally the wording the official server falls back to.
    /// </summary>
    private static string ResolveDescription(BotVerifierSettingsDocument settings, string? customDescription)
    {
        if (settings.CanModifyCustomDescription && !string.IsNullOrWhiteSpace(customDescription))
        {
            if (System.Text.Encoding.UTF8.GetByteCount(customDescription) > DescriptionLengthLimit)
            {
                throw new RpcException(new RpcError(400, "DESCRIPTION_TOO_LONG"));
            }

            return customDescription;
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomDescription))
        {
            return settings.CustomDescription;
        }

        return $"Was verified by organization \"{settings.Company}\"";
    }

    /// <summary>
    /// The method answers with a plain <c>Bool</c>, so without an update the badge only shows up
    /// after something else forces the peer to be refetched. Clients also decide between "verify"
    /// and "remove verification" from the cached <c>bot_verification_icon</c>, so a stale copy
    /// offers the wrong action.
    /// </summary>
    private async Task NotifyPeerChangedAsync(IRequestInput input, long targetUserId, long targetChannelId)
    {
        if (targetChannelId != 0)
        {
            await channelUpdateNotifier.NotifyChannelChangedAsync(input, targetChannelId);
            return;
        }

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateUser { UserId = targetUserId }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // The verified user learns about their own badge, and the verifier's other sessions refresh
        // the peer they just changed. The calling session already has the rpc result.
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, targetUserId), updates,
            excludeAuthKeyId: targetUserId == input.UserId ? input.AuthKeyId : null);

        if (targetUserId != input.UserId)
        {
            await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), updates,
                excludeAuthKeyId: input.AuthKeyId);
        }
    }
}
