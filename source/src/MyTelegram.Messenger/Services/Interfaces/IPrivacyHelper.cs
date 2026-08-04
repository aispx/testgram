using MyTelegram.Messenger.Services.Privacy;

namespace MyTelegram.Messenger.Services.Interfaces;

public interface IPrivacyHelper
{
    void ApplyPrivacy(
        IPrivacyReadModel? privacyReadModel,
        Action<PrivacyValueType> executeOnPrivacyNotMatch,
        long selfUserId,
        ContactType contactType);

    /// <inheritdoc cref="ApplyPrivacy(IPrivacyReadModel?, Action{PrivacyValueType}, long, ContactType)"/>
    /// <param name="viewerContext">
    /// Viewer facts needed by the premium / bots / close-friends / chat-participants rules.
    /// Overloads without it evaluate as <see cref="PrivacyViewerContext.Unknown"/>, under which
    /// those rules deny rather than grant access.
    /// </param>
    void ApplyPrivacy(
        IPrivacyReadModel? privacyReadModel,
        Action<PrivacyValueType> executeOnPrivacyNotMatch,
        long selfUserId,
        ContactType contactType,
        PrivacyViewerContext viewerContext);

    //void ApplyPrivacy(IPrivacyReadModel? privacyReadModel,
    //    Action executeOnPrivacyNotMatch,
    //    SimpleUserItem userItem,
    //    ContactType contactType);
    bool IsAllowedByPrivacy(long selfUserId, IPrivacyReadModel? privacyReadModel,
        ContactType contactType);

    /// <inheritdoc cref="IsAllowedByPrivacy(long, IPrivacyReadModel?, ContactType)"/>
    bool IsAllowedByPrivacy(long selfUserId, IPrivacyReadModel? privacyReadModel,
        ContactType contactType, PrivacyViewerContext viewerContext);
}