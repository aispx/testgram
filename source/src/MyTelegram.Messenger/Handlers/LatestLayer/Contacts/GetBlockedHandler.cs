namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Returns the list of blocked users.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.getBlocked"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBlockedHandler(
    IBlockCacheAppService blockCacheAppService,
    IUserAppService userAppService,
    IUserConverterService userConverterService,
    IPhotoAppService photoAppService,
    IPrivacyAppService privacyAppService,
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetBlocked, MyTelegram.Schema.Contacts.IBlocked>
{
    protected override async Task<MyTelegram.Schema.Contacts.IBlocked> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Contacts.RequestGetBlocked obj)
    {
        var limit = obj.Limit <= 0 ? 100 : Math.Min(obj.Limit, 1000);
        var page = await blockCacheAppService.GetBlockedAsync(input.UserId, obj.Offset, limit, obj.MyStoriesFrom);
        var blocked = page.Items
            .Select(p => new TPeerBlocked { PeerId = ToPeer(p), Date = p.Date })
            .Cast<IPeerBlocked>()
            .ToList();

        var userIds = page.Items
            .Where(p => p.TargetPeerType == PeerType.User)
            .Select(p => p.TargetPeerId)
            .Distinct()
            .ToList();
        var users = new List<ILayeredUser>();
        if (userIds.Count > 0)
        {
            var contactReadModels = await queryProcessor.ProcessAsync(new GetContactListQuery(input.UserId, userIds), CancellationToken.None);
            var userReadModels = await userAppService.GetListAsync(userIds);
            var privacyReadModels = await privacyAppService.GetPrivacyListAsync(userIds);
            var photos = await photoAppService.GetPhotosAsync(userReadModels, contactReadModels);
            users = userConverterService.ToUserList(input, userReadModels, photos, contactReadModels, privacyReadModels, input.Layer);
        }

        var channelIds = page.Items
            .Where(p => p.TargetPeerType == PeerType.Channel)
            .Select(p => p.TargetPeerId)
            .Distinct()
            .ToList();
        var chats = channelIds.Count == 0
            ? []
            : await chatConverterService.GetChannelListAsync(input, channelIds, layer: input.Layer);
        var basicChats = page.Items
            .Where(p => p.TargetPeerType == PeerType.Chat)
            .Select(p => p.TargetPeerId)
            .Distinct()
            .Select(p => new TChatForbidden { Id = p, Title = string.Empty })
            .Cast<IChat>();
        chats.AddRange(basicChats);

        if (obj.Offset + blocked.Count < page.Count)
        {
            return new TBlockedSlice
            {
                Count = page.Count,
                Blocked = [.. blocked],
                Chats = [.. chats],
                Users = [.. users],
            };
        }

        return new TBlocked
        {
            Blocked = [.. blocked],
            Chats = [.. chats],
            Users = [.. users],
        };
    }

    private static IPeer ToPeer(BlockedPeerCacheItem item) =>
        item.TargetPeerType switch
        {
            PeerType.Channel => new TPeerChannel { ChannelId = item.TargetPeerId },
            PeerType.Chat => new TPeerChat { ChatId = item.TargetPeerId },
            _ => new TPeerUser { UserId = item.TargetPeerId },
        };
}
