using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Create and optionally join a new conference call.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.createConferenceCall"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CreateConferenceCallHandler(
    IIdGenerator idGenerator,
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestCreateConferenceCall, MyTelegram.Schema.IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestCreateConferenceCall obj)
    {
        var existing = await _groupCallCollection
            .Find(g => g.CreatorId == input.UserId && g.RandomId == obj.RandomId && g.Conference)
            .FirstOrDefaultAsync();
        if (existing != null)
        {
            return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateCallUpdate(existing, input.UserId, peerHelper));
        }

        var callId = await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var call = new GroupCallDocument
        {
            Id = callId,
            CallId = callId,
            AccessHash = GroupCallStateHelper.CreateAccessHash(),
            RandomId = obj.RandomId,
            PeerId = input.UserId,
            PeerType = (int)PeerType.User,
            CreatorId = input.UserId,
            Conference = true,
            InviteHash = GroupCallStateHelper.CreateInviteHash(),
            Date = GroupCallStateHelper.CurrentDate()
        };
        call.InviteLink = TelegramDeepLinkHelper.GetConferenceCallLink(call.InviteHash);

        GroupCallParticipantDoc? participant = null;
        if (obj.Join)
        {
            participant = new GroupCallParticipantDoc
            {
                PeerId = input.UserId,
                PeerType = (int)PeerType.User,
                Source = Random.Shared.Next(100000, 999999),
                Muted = obj.Muted,
                VideoStopped = obj.VideoStopped,
                Date = call.Date,
                ParamsJson = obj.Params?.Data,
                PublicKey = obj.PublicKey?.ToArray()
            };
            call.Participants.Add(participant);
            if (obj.Block is { } block)
            {
                call.ChainBlocks.Add(new GroupCallChainBlockDoc { SubChainId = 0, Block = block.ToArray() });
            }
        }

        await _groupCallCollection.InsertOneAsync(call);

        var updates = new List<IUpdate> { GroupCallStateHelper.CreateCallUpdate(call, input.UserId, peerHelper) };
        if (participant != null)
        {
            updates.Add(GroupCallStateHelper.CreateParticipantsUpdate(call, input.UserId, peerHelper, [participant]));
        }
        if (obj.Join && obj.Block is { } chainBlock)
        {
            updates.Add(GroupCallStateHelper.CreateChainBlocksUpdate(call, 0, [chainBlock.ToArray()], call.ChainBlocks.Count(block => block.SubChainId == 0)));
        }

        return GroupCallStateHelper.Updates(updates.ToArray());
    }
}
