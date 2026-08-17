namespace MyTelegram.ReadModel.Interfaces;

public interface IChatInviteImporterReadModel : IReadModel
{
    string Id { get; }
    long PeerId { get; }
    long InviteId { get; }
    long UserId { get; }
    //bool RequestNeeded { get; }
    ChatInviteRequestState ChatInviteRequestState { get; }
    bool Approved { get; }
    long? ApprovedBy { get; }
    int Date { get; }
    string? About { get; }
    bool ViaChatList { get; }

    /// <summary>
    /// For <a href="https://corefork.telegram.org/api/subscriptions">Telegram Star subscriptions</a>,
    /// when the subscription bought through this invite link expires. Null for free invite links.
    /// </summary>
    int? SubscriptionUntilDate { get; }
}