using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using System.Net;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;
/// <summary>
/// Get info about RTMP streams in a group call or livestream.<br/>
/// This method should be invoked to the same group/channel-related DC used for <a href="https://corefork.telegram.org/api/files#downloading-files">downloading livestream chunks</a>.<br/>
/// As usual, the media DC is preferred, if available.
/// Possible errors
/// Code Type Description
/// 400 GROUPCALL_INVALID The specified group call is invalid.
/// 400 GROUPCALL_JOIN_MISSING You haven't joined this group call.
/// <para><c>See <a href="https://corefork.telegram.org/method/phone.getGroupCallStreamChannels"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetGroupCallStreamChannelsHandler(
    IMongoDatabase mongoDatabase,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options,
    ILogger<GetGroupCallStreamChannelsHandler> logger)
    : RpcResultObjectHandler<MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels, MyTelegram.Schema.Phone.IGroupCallStreamChannels>
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<MyTelegram.Schema.Phone.IGroupCallStreamChannels> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Phone.RequestGetGroupCallStreamChannels obj)
    {
        var groupCall = await _groupCallCollection.Find(GroupCallStateHelper.Filter(obj.Call, input.UserId)).FirstOrDefaultAsync();
        if (groupCall == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }
        if (!GroupCallStateHelper.IsJoinedByUser(groupCall, input.UserId) && groupCall.CreatorId != input.UserId)
        {
            RpcErrors.RpcErrors400.GroupcallJoinMissing.ThrowRpcError();
            return null!;
        }

        var channels = new TVector<IGroupCallStreamChannel>();
        if (await IsRtmpStreamOnlineAsync(groupCall))
        {
            channels.Add(new TGroupCallStreamChannel
            {
                Channel = 0,
                Scale = 0,
                LastTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        return new MyTelegram.Schema.Phone.TGroupCallStreamChannels
        {
            Channels = channels
        };
    }

    private async Task<bool> IsRtmpStreamOnlineAsync(GroupCallDocument groupCall)
    {
        if (!groupCall.RtmpStream || string.IsNullOrWhiteSpace(groupCall.RtmpStreamKey))
        {
            return false;
        }

        var hlsManifestUrl = BuildHlsManifestUrl(groupCall);
        if (hlsManifestUrl == null)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, hlsManifestUrl);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "RTMP stream manifest is not available for group call {CallId}, url: {Url}",
                groupCall.CallId,
                hlsManifestUrl);
            return false;
        }
    }

    private string? BuildHlsManifestUrl(GroupCallDocument groupCall)
    {
        var baseUrl = options.CurrentValue.RtmpHlsUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DeriveHlsBaseUrl(groupCall.RtmpUrl);
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(groupCall.RtmpStreamKey!)}/index.m3u8";
    }

    private static string? DeriveHlsBaseUrl(string? rtmpUrl)
    {
        if (string.IsNullOrWhiteSpace(rtmpUrl) || !Uri.TryCreate(rtmpUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttp,
            Port = 8888
        };

        return builder.Uri.ToString().TrimEnd('/');
    }
}
