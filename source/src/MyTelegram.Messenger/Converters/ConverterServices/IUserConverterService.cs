namespace MyTelegram.Messenger.Converters.ConverterServices;

public interface IUserConverterService
{
    /// <remarks>
    /// <paramref name="skipPrivacy"/> defaults to <c>false</c> so that callers get
    /// privacy-filtered users unless they explicitly opt out. It used to default to
    /// <c>true</c>, which silently leaked last seen, profile photos and phone numbers from
    /// every call site that forgot to pass the flag.
    /// </remarks>
    Task<ILayeredUser> GetUserAsync(IRequestWithAccessHashKeyId request, long userId, bool skipSetContactProperties = true,
        bool skipPrivacy = false, int layer = 0);

    /// <inheritdoc cref="GetUserAsync"/>
    Task<List<ILayeredUser>> GetUserListAsync(IRequestWithAccessHashKeyId request, List<long> userIds, bool skipSetContactProperties = true,
        bool skipCheckPrivacy = false, int layer = 0);

    Task<IUserFull> GetUserFullAsync(IRequestWithAccessHashKeyId request, long userId, int layer = 0);

    IUserFull ToUserFull(IRequestWithAccessHashKeyId request,
        IUserReadModel userReadModel,
        IReadOnlyCollection<IPhotoReadModel>? photoReadModels,
        IReadOnlyCollection<IContactReadModel>? contactReadModels,
        IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels, int layer = 0);
    ILayeredUser ToUser(IRequestWithAccessHashKeyId request, IUserReadModel userReadModel, IReadOnlyCollection<IPhotoReadModel>? photoReadModels = null,
        IContactReadModel? contactReadModel = null, IContactReadModel? targetUserContactReadModel = null, IReadOnlyCollection<IPrivacyReadModel>? privacyReadModels = null, int layer = 0);

    List<ILayeredUser> ToUserList(IRequestWithAccessHashKeyId request, IReadOnlyCollection<IUserReadModel> userReadModels,
        IReadOnlyCollection<IPhotoReadModel> photoReadModels,
        IReadOnlyCollection<IContactReadModel> contactReadModels,
        IReadOnlyCollection<IPrivacyReadModel> privacyReadModels, int layer = 0);
}