namespace MyTelegram;

public static class MessageForwardViewsHelper
{
    public static int? ResolveForwardedViews(bool isBroadcastDestination, MessageFwdHeader? fwdHeader)
    {
        return isBroadcastDestination && IsForwardedChannelPost(fwdHeader) ? 0 : null;
    }

    public static bool IsForwardedChannelPost(MessageFwdHeader? fwdHeader)
    {
        return fwdHeader?.FromId?.PeerType == PeerType.Channel &&
               fwdHeader.ChannelPost.HasValue;
    }
}
