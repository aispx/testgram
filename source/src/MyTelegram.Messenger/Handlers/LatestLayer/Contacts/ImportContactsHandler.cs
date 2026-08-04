namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Imports contacts: saves a full list on the server, adds already registered contacts to the contact list, returns added contacts and their info.Use <a href="https://corefork.telegram.org/method/contacts.addContact">contacts.addContact</a> to add Telegram contacts without actually using their phone number.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.importContacts"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ImportContactsHandler(ICommandBus commandBus, ICacheManager<UserCacheItem> cacheManager, IPrivacyAppService privacyAppService) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestImportContacts, MyTelegram.Schema.Contacts.IImportedContacts>
{
    protected override async Task<IImportedContacts> HandleCoreAsync(IRequestInput input, RequestImportContacts obj)
    {
        if (obj.Contacts.Count == 0)
        {
            RpcErrors.RpcErrors400.ContactIdInvalid.ThrowRpcError();
        }

        var keys = obj.Contacts.Select(p => UserCacheItem.GetCacheKey(p.Phone.ToPhoneNumber())).Distinct().ToList();
        var userIdDict = await cacheManager.GetManyAsync(keys);

        // privacyKeyAddedByPhone was only honoured by contacts.resolvePhone, so importing a
        // phone book still linked users who forbade being found by phone number. Those users
        // stay unresolved (userId 0): the contact is saved, but not connected to an account.
        var resolvedUserIds = userIdDict.Values
            .Select(p => p?.UserId ?? 0)
            .Where(p => p > 0 && p != input.UserId)
            .Distinct()
            .ToList();

        var restrictedUserIds = new HashSet<long>();
        if (resolvedUserIds.Count > 0)
        {
            await privacyAppService.ApplyPrivacyListAsync(input.UserId, resolvedUserIds,
                (_, restrictedUserId) => restrictedUserIds.Add(restrictedUserId),
                [PrivacyType.AddedByPhone]);
        }

        var phoneContactList = new List<PhoneContact>();
        foreach (var item in obj.Contacts)
        {
            var userId = 0L;
            var phone = item.Phone.ToPhoneNumber();
            var key = UserCacheItem.GetCacheKey(phone);
            if (userIdDict.TryGetValue(key, out var cachedUserItem))
            {
                userId = cachedUserItem?.UserId ?? 0;
            }

            if (restrictedUserIds.Contains(userId))
            {
                userId = 0;
            }

            if (userId != input.UserId)
            {
                phoneContactList.Add(new PhoneContact(userId, phone, item.FirstName, item.LastName, item.ClientId));
            }
        }

        if (phoneContactList.Count == 0)
        {
            RpcErrors.RpcErrors400.ContactIdInvalid.ThrowRpcError();
        }

        var command = new ImportContactsCommand(ImportedContactId.Create(input.UserId, "-"), input.ToRequestInfo(), input.UserId, phoneContactList);
        await commandBus.PublishAsync(command, default);
        return null !;
    }
}