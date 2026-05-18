namespace MyTelegram.Messenger.QueryServer.Services;

public interface IFileDownloadLaneRouter
{
    Task ForwardAsync(DownloadDataReceivedEvent eventData);
}
