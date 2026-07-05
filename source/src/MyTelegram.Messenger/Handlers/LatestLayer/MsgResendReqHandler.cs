namespace MyTelegram.Messenger.Handlers;

internal sealed class MsgResendReqHandler(ILogger<MsgResendReqHandler> logger) : BaseObjectHandler<TMsgResendReq, IObject>
{
    protected override Task<IObject> HandleCoreAsync(IRequestInput input,
        TMsgResendReq obj)
    {
        var requestedCount = obj.MsgIds?.Count ?? 0;
        var info = MessageStateInfoHelper.BuildProcessedWithResponseInfo(requestedCount);

        logger.LogInformation(
            "Responding to msg_resend_req with msgs_state_info: reqMsgId={ReqMsgId}, requestedCount={RequestedCount}, statusByte={StatusByte}",
            input.ReqMsgId,
            requestedCount,
            MessageStateInfoHelper.ProcessedWithResponseStatus);

        IObject r = new TMsgsStateInfo { Info = info, ReqMsgId = input.ReqMsgId };

        return Task.FromResult(r);
    }
}
