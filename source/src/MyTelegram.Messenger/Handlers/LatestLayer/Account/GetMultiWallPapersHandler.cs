using MyTelegram.Messenger.Services.WallPapers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Get info about multiple wallpapers
/// Possible errors
/// Code Type Description
/// 400 WALLPAPER_INVALID The specified wallpaper is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getMultiWallPapers"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
///
/// <para>The answer is positional: the caller passes wallpapers it is resolving for a theme and matches
/// them up by index. This used to return whatever Mongo happened to hand back, in Mongo's order, and to
/// silently drop anything it could not find — so a client received a shorter vector with no way to tell
/// which entry was missing.</para>
/// </remarks>
internal sealed class GetMultiWallPapersHandler(IWallPaperCatalog catalog)
    : RpcResultObjectHandler<MyTelegram.Schema.Account.RequestGetMultiWallPapers,
        TVector<MyTelegram.Schema.IWallPaper>>
{
    protected override async Task<TVector<MyTelegram.Schema.IWallPaper>> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Account.RequestGetMultiWallPapers obj)
    {
        if (obj.Wallpapers == null || obj.Wallpapers.Count == 0)
        {
            return new TVector<MyTelegram.Schema.IWallPaper>();
        }

        var ids = new List<long>();
        var slugs = new List<string>();

        foreach (var input1 in obj.Wallpapers)
        {
            switch (input1)
            {
                case MyTelegram.Schema.TInputWallPaper byId:
                    ids.Add(byId.Id);
                    break;
                case MyTelegram.Schema.TInputWallPaperNoFile noFile:
                    ids.Add(noFile.Id);
                    break;
                case MyTelegram.Schema.TInputWallPaperSlug bySlug:
                    slugs.Add(bySlug.Slug);
                    break;
            }
        }

        var rows = await catalog.FindManyAsync(ids, slugs);
        var byIdMap = rows.ToDictionary(p => p.WallPaperId);
        // A slug is not unique — it names the pattern image, and the same pattern is listed more than once
        // with different colours — so the lowest catalogue order wins, as it does in FindBySlugAsync.
        var bySlugMap = rows.Where(p => !string.IsNullOrEmpty(p.Slug))
            .GroupBy(p => p.Slug)
            .ToDictionary(p => p.Key, p => p.OrderBy(x => x.Order).ThenBy(x => x.WallPaperId).First());

        var result = new TVector<MyTelegram.Schema.IWallPaper>();

        foreach (var requested in obj.Wallpapers)
        {
            var row = requested switch
            {
                MyTelegram.Schema.TInputWallPaper byId => byIdMap.GetValueOrDefault(byId.Id),
                MyTelegram.Schema.TInputWallPaperNoFile noFile => byIdMap.GetValueOrDefault(noFile.Id),
                MyTelegram.Schema.TInputWallPaperSlug bySlug => bySlugMap.GetValueOrDefault(bySlug.Slug),
                _ => null
            };

            var wallPaper = row == null ? null : await catalog.BuildAsync(row, input.UserId);
            if (wallPaper == null)
            {
                RpcErrors.RpcErrors400.WallpaperInvalid.ThrowRpcError();
            }

            result.Add(wallPaper!);
        }

        return result;
    }
}
