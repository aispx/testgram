// ReSharper disable All

namespace MyTelegram.Schema.Account.LayerN;

/// <summary>
/// Create and upload a new <a href="https://corefork.telegram.org/api/wallpapers">wallpaper</a>.
///
/// <para><b>The second constructor of this method.</b> Layer 224 defines
/// <c>account.uploadWallPaper#e39a8f03 flags:# for_chat:flags.0?true file:InputFile mime_type:string
/// settings:WallPaperSettings = WallPaper</c>, which is what Telethon and tdesktop send; the generated
/// <c>LatestLayer</c> class carries the older <c>#dd853661</c>, which is what this fork's Android client
/// sends. Without this class the newer form has no parser at all, and
/// <c>HandlerHelper.TryGetHandler</c> throws <c>NotImplementedException</c> inside the fire-and-forget
/// <c>Task.Run</c> of <c>DefaultDataProcessor</c> — so the request is never answered, and the only trace is
/// a <c>Unsupported request, objectId: e39a8f03</c> warning.</para>
///
/// <para>The <c>LayerN</c> folder is where alternate constructors of a method live; the name says "older",
/// the mechanism is "forwards to the LatestLayer handler", which is what is needed here.</para>
///
/// <para>See <a href="https://corefork.telegram.org/method/account.uploadWallPaper" /></para>
/// </summary>
[TlObject(0xe39a8f03)]
public sealed class RequestUploadWallPaper : IRequest<MyTelegram.Schema.IWallPaper>
{
    public uint ConstructorId => 0xe39a8f03;

    /// <summary>
    /// Flags, see <a href="https://corefork.telegram.org/mtproto/TL-combinators#conditional-fields">TL conditional fields</a>
    /// </summary>
    public int Flags { get; set; }

    /// <summary>
    /// Set when uploading a wallpaper to be used with <c>messages.setChatWallPaper</c>.
    /// </summary>
    public bool ForChat { get; set; }

    /// <summary>
    /// The JPEG/PNG wallpaper
    /// See <a href="https://corefork.telegram.org/type/InputFile" />
    /// </summary>
    public MyTelegram.Schema.IInputFile File { get; set; }

    /// <summary>
    /// MIME type of uploaded wallpaper
    /// </summary>
    public string MimeType { get; set; }

    /// <summary>
    /// Wallpaper settings
    /// See <a href="https://corefork.telegram.org/type/WallPaperSettings" />
    /// </summary>
    public MyTelegram.Schema.IWallPaperSettings Settings { get; set; }

    public void ComputeFlag()
    {
        if (ForChat) { Flags = Flags.SetBit(0); }
    }

    public void Serialize(IBufferWriter<byte> writer)
    {
        ComputeFlag();
        writer.Write(ConstructorId);
        writer.Write(Flags);
        writer.Write(File);
        writer.Write(MimeType);
        writer.Write(Settings);
    }

    public void Deserialize(ref ReadOnlyMemory<byte> buffer)
    {
        Flags = buffer.ReadInt32();
        if (Flags.IsBitSet(0)) { ForChat = true; }
        File = buffer.Read<MyTelegram.Schema.IInputFile>();
        MimeType = buffer.ReadString();
        Settings = buffer.Read<MyTelegram.Schema.IWallPaperSettings>();
    }
}
