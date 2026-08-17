using MyTelegram.Messenger.Converters.ConverterServices;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

/// <summary>
/// Builds a <c>chatInviteExported</c> from the read model and fills in the fields that are derived
/// from other read models rather than stored on the invite itself.
/// </summary>
internal static class ChatInviteExportedFiller
{
    public static async Task<MyTelegram.Schema.IExportedChatInvite> ToExportedChatInviteAsync(
        IChatInviteExportedConverterService converterService,
        IQueryProcessor queryProcessor,
        IChatInviteReadModel readModel,
        int layer)
    {
        var exported = converterService.ToExportedChatInvite(readModel, layer);

        // subscription_expired counts members that joined through a paid link and have since let
        // their Star subscription lapse. It is only meaningful for paid links.
        if (readModel.SubscriptionPricingAmount is > 0 && exported is TChatInviteExported tExported)
        {
            tExported.SubscriptionExpired = await queryProcessor.ProcessAsync(
                new GetChatInviteImporterCountQuery(readModel.PeerId, readModel.InviteId, null, true));
        }

        return exported;
    }

    /// <summary>
    /// The link a join request came in through, as it has to be reported in the admin log. Requests
    /// made through a public username carry no link at all, which the API models with
    /// <c>chatInvitePublicJoinRequests</c>.
    /// </summary>
    public static async Task<MyTelegram.Schema.IExportedChatInvite> ToRequestInviteAsync(
        IChatInviteExportedConverterService converterService,
        IQueryProcessor queryProcessor,
        long peerId,
        long? inviteId,
        int layer)
    {
        if (inviteId is not > 0)
        {
            return new TChatInvitePublicJoinRequests();
        }

        var readModel = await queryProcessor.ProcessAsync(new GetChatInviteByInviteIdQuery(peerId, inviteId.Value));

        return readModel == null
            ? new TChatInvitePublicJoinRequests()
            : await ToExportedChatInviteAsync(converterService, queryProcessor, readModel, layer);
    }
}
