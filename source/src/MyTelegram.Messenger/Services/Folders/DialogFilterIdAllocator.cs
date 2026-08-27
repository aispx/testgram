namespace MyTelegram.Messenger.Services.Folders;

/// <summary>
/// Picks the folder id an imported folder gets on the importing account.
///
/// <para>Folder ids are per user and are normally chosen by the client, starting at 2. A folder imported from
/// a <a href="https://corefork.telegram.org/api/links#chat-folder-links">chat folder link</a> is created by
/// the server instead, so the server has to pick a free number: reusing the exporter's <c>filter_id</c>, as
/// this used to, overwrites whichever folder of the importer happens to carry the same number.</para>
/// </summary>
public interface IDialogFilterIdAllocator
{
    Task<int> AllocateAsync(long userId);
}

/// <inheritdoc />
public class DialogFilterIdAllocator(IQueryProcessor queryProcessor) : IDialogFilterIdAllocator, ITransientDependency
{
    public async Task<int> AllocateAsync(long userId)
    {
        var filters = await queryProcessor.ProcessAsync(new GetDialogFiltersQuery(userId));
        var used = filters.Select(p => p.Filter.Id).ToHashSet();

        var filterId = DialogFilterValidator.MinFilterId;
        while (used.Contains(filterId))
        {
            filterId++;
        }

        return filterId;
    }
}
