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
}
