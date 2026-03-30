using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;

public class ForwardMessagesLegacyHandler(ForwardMessagesHandler forwardMessagesHandler) : RpcResultObjectHandler<RequestForwardMessagesLegacy, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestForwardMessagesLegacy obj)
    {
        var newRequest = new RequestForwardMessages
        {
            Flags = obj.Flags,
            Silent = obj.Silent,
            Background = obj.Background,
            WithMyScore = obj.WithMyScore,
            DropAuthor = obj.DropAuthor,
            DropMediaCaptions = obj.DropMediaCaptions,
            Noforwards = obj.Noforwards,
            FromPeer = obj.FromPeer,
            Id = obj.Id,
            RandomId = obj.RandomId,
            ToPeer = obj.ToPeer,
            TopMsgId = obj.TopMsgId,
            ScheduleDate = obj.ScheduleDate,
            ScheduleRepeatPeriod = obj.ScheduleRepeatPeriod,
            SendAs = obj.SendAs,
            QuickReplyShortcut = obj.QuickReplyShortcut,
            ReplyTo = obj.ReplyTo,
            VideoTimestamp = obj.VideoTimestamp,
            AllowPaidStars = obj.AllowPaidStars,
            SuggestedPost = obj.SuggestedPost
        };

        return await forwardMessagesHandler.HandleAsync(input, newRequest);
    }
}
