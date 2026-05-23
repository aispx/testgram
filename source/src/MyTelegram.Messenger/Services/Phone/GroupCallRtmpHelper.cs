namespace MyTelegram.Messenger.Services.Phone;

internal static class GroupCallRtmpHelper
{
    private const string DefaultRtmpStreamUrl = "rtmp://testgram.xie.su:1935/live";

    public static string GetRtmpStreamUrl(MyTelegramMessengerServerOptions options)
    {
        return string.IsNullOrWhiteSpace(options.RtmpStreamUrl)
            ? DefaultRtmpStreamUrl
            : options.RtmpStreamUrl.TrimEnd('/');
    }

    public static string GetStoredOrDefault(string? url, string defaultUrl)
    {
        return string.IsNullOrWhiteSpace(url)
            ? defaultUrl
            : url.TrimEnd('/');
    }

    public static string GetStreamId(long peerId, int peerType, bool liveStory = false)
    {
        return liveStory
            ? $"live:{peerType}:{peerId}"
            : $"call:{peerType}:{peerId}";
    }

    public static string CreateStreamKey()
    {
        return Guid.NewGuid().ToString("N");
    }
}
