using MyTelegram.Messenger.Converters.ConverterServices.Messages;
using MyTelegram.Messenger.Helpers;
using MyTelegram.Messenger.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get live location history of a certain user: up to one active
/// <a href="https://corefork.telegram.org/constructor/messageMediaGeoLive">messageMediaGeoLive</a>
/// per chat participant.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getRecentLocations"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetRecentLocationsHandler(
    IPeerHelper peerHelper,
    IQueryProcessor queryProcessor,
    IChannelAppService channelAppService,
    IMessageAppService messageAppService,
    IGetHistoryConverterService getHistoryConverterService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentLocations, MyTelegram.Schema.Messages.IMessages>
{
    /// <summary>Upper bound on the number of returned locations, matching the history paging limit.</summary>
    private const int MaxLimit = 100;

    protected override async Task<MyTelegram.Schema.Messages.IMessages> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestGetRecentLocations obj)
    {
        var userId = input.UserId;
        var peer = peerHelper.GetPeer(obj.Peer, userId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : userId;

        if (peer.PeerType == PeerType.Channel)
        {
            var channelMember = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(peer.PeerId, userId));
            if (channelMember?.Kicked == true)
            {
                return Empty();
            }

            var channelReadModel = await channelAppService.GetAsync(peer.PeerId);
            if (channelReadModel != null &&
                await channelAppService.SendRpcErrorIfNoReadAccessAsync(input, channelReadModel))
            {
                return null!;
            }
        }

        var limit = obj.Limit is <= 0 or > MaxLimit ? MaxLimit : obj.Limit;

        // Page over live locations only. Filtering in the query matters twice over: a live location
        // stays valid for up to 24h so in a busy chat it sits far behind the newest messages, and
        // MessageType.Geo on its own would also match static locations and venues, which could fill
        // the whole window on their own.
        var output = await messageAppService.GetHistoryAsync(new GetHistoryInput
        {
            OwnerPeerId = ownerPeerId,
            SelfUserId = userId,
            Limit = MaxLimit,
            Peer = peer,
            MessageType = MessageType.Geo,
            GeoLiveOnly = true
        });

        var now = CurrentDate;

        // One location per participant, newest first. Picking the max message id explicitly rather
        // than trusting the order the history query happens to return keeps this correct regardless
        // of how that query sorts.
        var locations = output.MessageList
            .Where(p => p.Media2 is TMessageMediaGeoLive geoLive && GeoLiveHelper.IsActive(geoLive, p.Date, now))
            .GroupBy(p => p.SenderUserId)
            .Select(g => g.MaxBy(p => p.MessageId)!)
            .OrderByDescending(p => p.MessageId)
            .Take(limit)
            .ToList();

        // hash lets the client skip a response it already has (messages.Messages semantics): the
        // server returns messagesNotModified when the computed hash matches the one the client sent.
        var hash = locations.Aggregate(0L, (current, message) => MessageSearchMongoHelper.CalcHash(current, message.MessageId));
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TMessagesNotModified { Count = locations.Count };
        }

        output.MessageList = locations;
        // This method returns the complete set of active locations rather than a page of history, so
        // the converter's "a full page means there is more" heuristic must not fire and turn the
        // result into a messagesSlice with a paging cursor.
        output.Limit = locations.Count + 1;
        return getHistoryConverterService.ToMessages(input, output, input.Layer);
    }

    private static TMessages Empty() => new()
    {
        Chats = new TVector<IChat>(),
        Messages = new TVector<IMessage>(),
        Users = new TVector<IUser>(),
        Topics = new TVector<IForumTopic>()
    };
}
