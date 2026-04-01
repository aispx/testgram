namespace MyTelegram.Messenger.Handlers.LatestLayer.Stickers;
/// <summary>
/// Suggests a short name for a given stickerpack name
/// Possible errors
/// Code Type Description
/// 400 TITLE_INVALID The specified stickerpack title is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/stickers.suggestShortName"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SuggestShortNameHandler : RpcResultObjectHandler<MyTelegram.Schema.Stickers.RequestSuggestShortName, MyTelegram.Schema.Stickers.ISuggestedShortName>
{
    protected override Task<MyTelegram.Schema.Stickers.ISuggestedShortName> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Stickers.RequestSuggestShortName obj)
    {
        // Validate title: must not start with digit
        if (!string.IsNullOrEmpty(obj.Title) && char.IsDigit(obj.Title[0]))
        {
            RpcErrors.RpcErrors400.TitleInvalid.ThrowRpcError();
        }

        // Generate short_name from title
        // Replace spaces with underscores, keep only alphanumeric and underscores, convert to lowercase
        var shortName = new string(obj.Title
            .Replace(' ', '_')
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_')
            .Take(32)
            .ToArray());

        // Remove leading/trailing underscores
        shortName = shortName.Trim('_');

        // If empty after filtering, throw error
        if (string.IsNullOrEmpty(shortName))
        {
            RpcErrors.RpcErrors400.TitleInvalid.ThrowRpcError();
        }

        // Add random suffix (3 digits like in original Telegram)
        shortName += Random.Shared.Next(100, 999);

        return Task.FromResult<MyTelegram.Schema.Stickers.ISuggestedShortName>(
            new MyTelegram.Schema.Stickers.TSuggestedShortName
            {
                ShortName = shortName
            });
    }
}
 
