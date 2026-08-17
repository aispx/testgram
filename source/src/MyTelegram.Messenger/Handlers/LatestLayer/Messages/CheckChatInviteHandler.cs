using MongoDB.Driver;
using MyTelegram.Messenger.Services.StarsSubscriptions;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Check the validity of a chat invite link and get basic info about it
/// Possible errors
/// Code Type Description
/// 406 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 INVITE_HASH_EMPTY The invite hash is empty.
/// 406 INVITE_HASH_EXPIRED The invite link has expired.
/// 400 INVITE_HASH_INVALID The invite hash is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.checkChatInvite"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CheckChatInviteHandler(IQueryProcessor queryProcessor, IPhotoAppService photoAppService, IChannelAppService channelAppService, IChatConverterService chatConverterService, IUserConverterService userConverterService, ILayeredService<IPhotoConverter> layeredPhotoService, IStarsSubscriptionService starsSubscriptionService, IChatInvitePeekService chatInvitePeekService, MongoDB.Driver.IMongoDatabase mongoDatabase) : RpcResultObjectHandler<RequestCheckChatInvite, IChatInvite>
{
    private async Task<MyTelegram.Schema.IBotVerification?> GetBotVerificationAsync(long channelId)
    {
        var col = mongoDatabase.GetCollection<MyTelegram.Messenger.Services.BotVerificationDocument>("bot-verifications");
        var doc = await col.Find(MongoDB.Driver.Builders<MyTelegram.Messenger.Services.BotVerificationDocument>.Filter.Eq(x => x.ChannelId, channelId)).FirstOrDefaultAsync();
        if (doc == null) return null;
        return new TBotVerification { BotId = doc.BotId, Icon = doc.Icon, Description = doc.Description };
    }

    /// <summary>
    /// A few of the members, shown as avatars on the join sheet before the user commits.
    /// </summary>
    private async Task<TVector<IUser>> GetParticipantsPreviewAsync(IRequestInput input, long channelId)
    {
        var members = await queryProcessor.ProcessAsync(new GetChannelMembersByChannelIdQuery(channelId, [], 0, ParticipantsPreviewCount));
        var userIds = members.Select(p => p.UserId).Distinct().ToList();
        if (userIds.Count == 0)
        {
            return new TVector<IUser>();
        }

        var users = await userConverterService.GetUserListAsync(input, userIds, false, false, input.Layer);

        return [.. users];
    }

    private const int ParticipantsPreviewCount = 10;

    protected override async Task<IChatInvite> HandleCoreAsync(IRequestInput input, RequestCheckChatInvite obj)
    {
        if (string.IsNullOrEmpty(obj.Hash))
        {
            RpcErrors.RpcErrors400.InviteHashEmpty.ThrowRpcError();
        }

        var chatInviteReadModel = await queryProcessor.ProcessAsync(new GetChatInviteByLinkQuery(obj.Hash));
        if (chatInviteReadModel == null)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        if (chatInviteReadModel!.ExpireDate > 0)
        {
            if (chatInviteReadModel.ExpireDate.Value < CurrentDate)
            {
                RpcErrors.RpcErrors400.InviteHashExpired.ThrowRpcError();
            }
        }

        if (chatInviteReadModel.Revoked)
        {
            RpcErrors.RpcErrors400.InviteHashExpired.ThrowRpcError();
        }

        var channelReadModel = await channelAppService.GetAsync(chatInviteReadModel.PeerId);
        if (channelReadModel == null !)
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        var chatPhoto = await photoAppService.GetAsync(channelReadModel!.PhotoId);
        var channelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelReadModel!.ChannelId, input.UserId));
        if (channelMemberReadModel is { Kicked: true })
        {
            RpcErrors.RpcErrors400.InviteHashInvalid.ThrowRpcError();
        }

        // Public channel/Super group
        if (!string.IsNullOrEmpty(channelReadModel.UserName) || channelMemberReadModel is { Left: false, Kicked: false })
        {
            if (channelMemberReadModel != null)
            {
                var channel = chatConverterService.ToChannel(input, channelReadModel, chatPhoto, channelMemberReadModel, null, input.Layer);
                return new TChatInviteAlready
                {
                    Chat = channel
                };
            }
        }

        // The link may instead grant a read-only preview of the chat: the caller can then read the
        // history for a while and decide whether to join. Links that deliberately gate access -
        // admin approval or a Star subscription - and chats that hide their history from
        // non-members are never previewable.
        // See https://corefork.telegram.org/api/invites
        if (!chatInviteReadModel.RequestNeeded &&
            chatInviteReadModel.SubscriptionPricingAmount is not > 0 &&
            !channelReadModel.HiddenPreHistory)
        {
            var peek = await chatInvitePeekService.GrantAsync(input.UserId, channelReadModel.ChannelId,
                chatInviteReadModel.Link);

            return new TChatInvitePeek
            {
                Chat = chatConverterService.ToChannel(input, channelReadModel, chatPhoto, null, null, input.Layer),
                Expires = peek.Expires
            };
        }

        // A paid link either shows the price, or - when the buyer already paid and has not let the
        // subscription lapse - lets them re-join for free.
        TStarsSubscriptionPricing? subscriptionPricing = null;
        var canRefulfillSubscription = false;
        long? subscriptionFormId = null;
        if (chatInviteReadModel.SubscriptionPricingAmount is > 0 && chatInviteReadModel.SubscriptionPricingPeriod is > 0)
        {
            subscriptionPricing = new TStarsSubscriptionPricing
            {
                Period = chatInviteReadModel.SubscriptionPricingPeriod.Value,
                Amount = chatInviteReadModel.SubscriptionPricingAmount.Value
            };

            var subscription = await starsSubscriptionService.GetActiveSubscriptionAsync(input.UserId, channelReadModel.ChannelId);
            canRefulfillSubscription = subscription != null;
            if (!canRefulfillSubscription)
            {
                // Form ids are not persisted: payments.sendStarsForm resolves the invite from the
                // hash, so this only has to be unique for the client's own bookkeeping.
                subscriptionFormId = Random.Shared.NextInt64();
            }
        }

        return new TChatInvite
        {
            About = channelReadModel.About,
            Broadcast = channelReadModel.Broadcast,
            Channel = true,
            Public = !string.IsNullOrEmpty(channelReadModel.UserName),
            Megagroup = channelReadModel.MegaGroup,
            ParticipantsCount = channelReadModel.ParticipantsCount ?? 0,
            Participants = await GetParticipantsPreviewAsync(input, channelReadModel.ChannelId),
            Photo = layeredPhotoService.GetConverter(input.Layer).ToPhoto(chatPhoto),
            RequestNeeded = chatInviteReadModel.RequestNeeded,
            Title = channelReadModel.Title,
            Verified = channelReadModel.Verified,
            Scam = channelReadModel.Scam,
            Fake = channelReadModel.Fake,
            Color = channelReadModel.Color?.Color ?? 0,
            SubscriptionPricing = subscriptionPricing,
            SubscriptionFormId = subscriptionFormId,
            CanRefulfillSubscription = canRefulfillSubscription,
            BotVerification = await GetBotVerificationAsync(channelReadModel.ChannelId),
        };
    }
}