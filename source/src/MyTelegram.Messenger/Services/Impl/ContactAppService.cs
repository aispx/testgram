namespace MyTelegram.Messenger.Services.Impl;

public interface IContactHelper
{
    ContactType GetContactType(IContactReadModel? myContactReadModel,
        IContactReadModel? targetUserContactReadModel);

    ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels);
}

public class ContactHelper : IContactHelper, ITransientDependency
{
    public ContactType GetContactType(IContactReadModel? myContactReadModel,
        IContactReadModel? targetUserContactReadModel)
    {
        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }

    public ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels)
    {
        var myContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);

        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }
}

public class ContactAppService(
    IQueryProcessor queryProcessor,
    IPhotoAppService photoAppService,
    IChannelAppService channelAppService,
    IUserAppService userAppService,
    IPeerHelper peerHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : BaseAppService, IContactAppService, ITransientDependency
{
    public ContactType GetContactType(long selfUserId, long targetUserId,
        IReadOnlyCollection<IContactReadModel> contactReadModels)
    {
        var myContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == selfUserId && p.TargetUserId == targetUserId);
        var targetUserContactReadModel =
            contactReadModels.FirstOrDefault(p => p.SelfUserId == targetUserId && p.TargetUserId == selfUserId);

        var contactType = (myContactReadModel, targetUserContactReadModel)
            switch
        {
            { myContactReadModel: not null, targetUserContactReadModel: not null } => ContactType.Mutual,
            { myContactReadModel: null, targetUserContactReadModel: not null } => ContactType
                .ContactOfTargetUser,
            { myContactReadModel: not null, targetUserContactReadModel: null } => ContactType
                .TargetUserIsMyContact,
            _ => ContactType.None
        };

        return contactType;
    }

    public async Task<ContactType> GetContactTypeAsync(long selfUserId, long targetUserId)
    {
        var contactReadModels =
            await queryProcessor.ProcessAsync(new GetContactListBySelfIdAndTargetUserIdQuery(selfUserId, targetUserId));

        return GetContactType(selfUserId, targetUserId, contactReadModels);
    }

    public async Task<SearchContactOutput> SearchAsync(long selfUserId,
        string keyword, int limit)
    {
        // Changed from > 4 to >= 1 to allow shorter searches like "chat"
        if (keyword?.Length >= 1)
        {
            var searchKeyword = keyword;
            if (searchKeyword.StartsWith("@"))
            {
                searchKeyword = keyword[1..];
            }

            var defaultLimit = limit;
            if (defaultLimit <= 0 || defaultLimit > 1000)
            {
                defaultLimit = 20;
            }

            var contactReadModels = await queryProcessor
                .ProcessAsync(new SearchContactQuery(selfUserId, searchKeyword));
            var userNameReadModels = await queryProcessor
                .ProcessAsync(new SearchUserNameQuery(searchKeyword));

            // Collect channel IDs from username search and keyword search
            var channelIdList = userNameReadModels.Where(p => p.PeerType == PeerType.Channel).Select(p => p.PeerId)
                .ToList();
            var channelIds2 =
                await queryProcessor.ProcessAsync(new GetChannelIdsByKeywordQuery(selfUserId, keyword, defaultLimit));
            channelIdList.AddRange(channelIds2);
            channelIdList = channelIdList.Distinct().ToList();

            // Collect user IDs from contacts and username search (includes both users and bots)
            var userIdList = contactReadModels.Select(p => p.TargetUserId).ToList();
            userIdList.AddRange(userNameReadModels.Where(p => p.PeerType == PeerType.User).Select(p => p.PeerId));

            var userReadModels = await userAppService.GetListAsync(userIdList);
            var allUserReadModels = userReadModels.ToList();

            if (options.CurrentValue.EnableSearchNonContacts)
            {
                var userReadModels2 =
                    await queryProcessor.ProcessAsync(new SearchUserByKeywordQuery(keyword, defaultLimit));
                allUserReadModels.AddRange(userReadModels2);
                allUserReadModels = allUserReadModels.DistinctBy(p => p.UserId).ToList();
            }

            var channelReadModels = await channelAppService.GetListAsync(channelIdList);

            // Also search for chats (groups) by keyword
            var chatReadModels = new List<IChannelReadModel>();
            try
            {
                var chatIds = await queryProcessor.ProcessAsync(new GetChannelIdsByKeywordQuery(selfUserId, keyword, defaultLimit));
                if (chatIds.Any())
                {
                    var chats = await channelAppService.GetListAsync(chatIds);
                    chatReadModels = chats.Where(c => !c.Broadcast).ToList(); // Groups are non-broadcast channels
                }
            }
            catch
            {
                // Ignore errors in chat search
            }

            var photoReadModels =
                await photoAppService.GetPhotosAsync(allUserReadModels, contactReadModels, channelReadModels);

            return new SearchContactOutput(selfUserId,
                allUserReadModels,
                photoReadModels,
                contactReadModels,
                chatReadModels,
                channelReadModels,
                [],
                []
            );
        }

        return new SearchContactOutput(selfUserId,
            new List<IUserReadModel>(),
            new List<IPhotoReadModel>(),
            new List<IContactReadModel>(),
            new List<IChannelReadModel>(),
            new List<IChannelReadModel>(),
            new List<IPrivacyReadModel>(),
            new List<IChannelMemberReadModel>());
    }
}