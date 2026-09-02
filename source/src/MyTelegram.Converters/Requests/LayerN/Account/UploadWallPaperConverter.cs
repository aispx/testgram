using MyTelegram.Schema.Account.LayerN;

namespace MyTelegram.Converters.Requests.LayerN.Account;

/// <summary>
/// Maps <c>account.uploadWallPaper#e39a8f03</c> onto the constructor the handler is written against.
///
/// <para><c>for_chat</c> is dropped, and there is nothing to drop it into: the generated
/// <c>LatestLayer</c> request has no such field, and nothing in this repository gates a wallpaper on it.
/// The API says the flag "must be set when uploading wallpapers to be used with
/// messages.setChatWallPaper", but this fork's Android client uploads chat wallpapers without it, so
/// enforcing it would refuse a request the official client makes.</para>
/// </summary>
internal sealed class UploadWallPaperConverter
    : IRequestConverter<
        RequestUploadWallPaper,
        Schema.Account.RequestUploadWallPaper
    >, ITransientDependency
{
    public Schema.Account.RequestUploadWallPaper ToLatestLayerData(IRequestInput request,
        RequestUploadWallPaper obj)
    {
        return new Schema.Account.RequestUploadWallPaper
        {
            File = obj.File,
            MimeType = obj.MimeType,
            Settings = obj.Settings
        };
    }
}
