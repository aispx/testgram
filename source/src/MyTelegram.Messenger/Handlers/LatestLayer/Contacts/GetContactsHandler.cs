namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Returns the current user's contact list.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.getContacts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetContactsHandler(IQueryProcessor queryProcessor, IUserConverterService userConverterService, IPhotoAppService photoAppService, IHashCalculator hashCalculator, IUserAppService userAppService, IPrivacyAppService privacyAppService) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetContacts, MyTelegram.Schema.Contacts.IContacts>
{
    protected override async Task<IContacts> HandleCoreAsync(IRequestInput input, RequestGetContacts obj)
    {
        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactsByUserIdQuery(input.UserId), CancellationToken.None);
        var userIdList = contactReadModels.Select(p => p.TargetUserId).ToList();
        var conversionContactReadModels = await queryProcessor.ProcessAsync(new GetContactListQuery(input.UserId, userIdList), CancellationToken.None);

        userIdList.Add(input.UserId);
        var userReadModels = await userAppService.GetListAsync(userIdList);
        var privacyReadModels = await privacyAppService.GetPrivacyListAsync(userIdList);
        var photos = await photoAppService.GetPhotosAsync(userReadModels, conversionContactReadModels);
        var userList = userConverterService.ToUserList(input, userReadModels, photos, conversionContactReadModels, privacyReadModels, input.Layer);
        var validUserIds = new List<long>();
        foreach (var user in userList)
        {
            user.Contact = true;
            validUserIds.Add(user.Id);
        }

        var hash = hashCalculator.GetHash(userIdList);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TContactsNotModified();
        }

        var mutualContactUserIds = conversionContactReadModels
            .Where(p => p.TargetUserId == input.UserId)
            .Select(p => p.SelfUserId)
            .ToHashSet();
        var contacts = new TContacts
        {
            Contacts = [..contactReadModels.Where(p => validUserIds.Contains(p.TargetUserId)).Select(p => new TContact { UserId = p.TargetUserId, Mutual = mutualContactUserIds.Contains(p.TargetUserId) })],
            Users = [..userList],
            SavedCount = contactReadModels.Count,
        };
        return contacts;
    }
}