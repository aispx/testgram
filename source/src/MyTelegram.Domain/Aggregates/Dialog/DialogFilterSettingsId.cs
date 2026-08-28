namespace MyTelegram.Domain.Aggregates.Dialog;

/// <summary>
/// One row per user: the order of their <a href="https://corefork.telegram.org/api/folders">folders</a>,
/// the folder tags toggle and whether the chat archive is pinned.
/// </summary>
[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<DialogFilterSettingsId>))]
public class DialogFilterSettingsId(string value) : Identity<DialogFilterSettingsId>(value)
{
    public static DialogFilterSettingsId Create(long ownerUserId)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands,
            $"dialogfiltersettings_{ownerUserId}");
    }
}
