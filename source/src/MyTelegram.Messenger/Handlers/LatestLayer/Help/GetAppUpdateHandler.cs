using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;

internal sealed class GetAppUpdateHandler : RpcResultObjectHandler<MyTelegram.Schema.Help.RequestGetAppUpdate, MyTelegram.Schema.Help.IAppUpdate>
{
    private const string UpdateJsonPath = "/app/update.json";

    protected override Task<MyTelegram.Schema.Help.IAppUpdate> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Help.RequestGetAppUpdate obj)
    {
        try
        {
            if (File.Exists(UpdateJsonPath))
            {
                var json = JsonDocument.Parse(File.ReadAllText(UpdateJsonPath)).RootElement;
                return Task.FromResult<IAppUpdate>(new TAppUpdate
                {
                    Id = json.GetProperty("version_code").GetInt32(),
                    Version = json.GetProperty("version").GetString() ?? "",
                    Text = json.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "",
                    Entities = [],
                    Url = json.GetProperty("file_url").GetString() ?? "",
                });
            }
        }
        catch { }

        return Task.FromResult<IAppUpdate>(new TNoAppUpdate());
    }
}
