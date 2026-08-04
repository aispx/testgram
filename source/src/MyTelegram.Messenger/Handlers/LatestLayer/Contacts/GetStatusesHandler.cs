using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Use this method to obtain the online statuses of all contacts with an accessible Telegram account.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.getStatuses"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetStatusesHandler(IUserStatusCacheAppService userStatusAppService, IQueryProcessor queryProcessor, IPrivacyAppService privacyAppService) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestGetStatuses, TVector<MyTelegram.Schema.IContactStatus>>
{
    protected override async Task<TVector<IContactStatus>> HandleCoreAsync(IRequestInput input, RequestGetStatuses obj)
    {
        var contactReadModels = await queryProcessor.ProcessAsync(new GetContactsByUserIdQuery(input.UserId), default);
        var targetUserIds = contactReadModels.Select(p => p.TargetUserId).Distinct().ToList();

        // This method bypassed privacyKeyStatusTimestamp entirely: exact last seen timestamps
        // were returned for every contact, even those who hid them. Users who disallowed us
        // are coarsened to userStatusRecently, as on the user-object path.
        var restrictedUserIds = new HashSet<long>();
        if (targetUserIds.Count > 0)
        {
            await privacyAppService.ApplyPrivacyListAsync(input.UserId, targetUserIds,
                (_, restrictedUserId) => restrictedUserIds.Add(restrictedUserId),
                [PrivacyType.StatusTimestamp]);
        }

        var statusList = new List<IContactStatus>();
        foreach (var contactReadModel in contactReadModels)
        {
            var status = userStatusAppService.GetUserStatus(contactReadModel.TargetUserId);
            if (restrictedUserIds.Contains(contactReadModel.TargetUserId))
            {
                status = PrivacyMaskingHelper.MaskStatusTimestamp(status);
            }

            statusList.Add(new TContactStatus { Status = status, UserId = contactReadModel.TargetUserId });
        }

        return[..statusList];
    }
}