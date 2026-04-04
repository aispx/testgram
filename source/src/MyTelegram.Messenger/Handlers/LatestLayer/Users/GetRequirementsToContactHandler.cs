using MyTelegram.Messenger.Services;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Check whether we can write to the specified users, used to implement bulk checks for <a href="https://corefork.telegram.org/api/privacy#require-premium-for-new-non-contact-users">Premium-only messages »</a> and <a href="https://corefork.telegram.org/api/paid-messages">paid messages »</a>.For each input user, returns a <a href="https://corefork.telegram.org/type/RequirementToContact">RequirementToContact</a> constructor (at the same offset in the vector) containing requirements to contact them.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getRequirementsToContact"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRequirementsToContactHandler(
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IUserAppService userAppService) : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetRequirementsToContact, TVector<MyTelegram.Schema.IRequirementToContact>>
{
    protected override async Task<TVector<MyTelegram.Schema.IRequirementToContact>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetRequirementsToContact obj)
    {
        var result = new TVector<IRequirementToContact>();

        foreach (var inputUser in obj.Id)
        {
            try
            {
                // Validate access hash
                await accessHashHelper.CheckAccessHashAsync(input, inputUser);

                // Get target user
                var targetPeer = peerHelper.GetPeer(inputUser, input.UserId);
                var targetUserId = targetPeer.PeerId;

                // Get user info
                var userReadModel = await userAppService.GetAsync(targetUserId);

                if (userReadModel == null)
                {
                    // User not found - return empty requirement
                    result.Add(new TRequirementToContactEmpty());
                    continue;
                }

                // Check if user requires premium to contact
                // This would be stored in user settings/privacy settings
                // For now, return empty (no requirements)
                // In full implementation, check:
                // 1. If user has "require premium for new contacts" enabled
                // 2. If user requires paid messages (stars_amount)

                result.Add(new TRequirementToContactEmpty());
            }
            catch
            {
                // If any error, return empty requirement
                result.Add(new TRequirementToContactEmpty());
            }
        }

        return result;
    }
}