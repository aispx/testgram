using MyTelegram.Messenger.Services.Localization;

namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Resolves the language a server-generated message should be written in for a given user.
/// The language comes from the <c>lang_code</c> the client reported in
/// <a href="https://corefork.telegram.org/method/initConnection">initConnection</a>, which is
/// stored on the device read model.
/// </summary>
public interface IUserLanguageResolver
{
    /// <summary>
    /// Two-letter language code of the user's most recently active device, lower-cased.
    /// Falls back to <see cref="ServerLanguage.Default"/> when the user has no device with a
    /// usable <c>lang_code</c>.
    /// </summary>
    Task<string> GetLanguageAsync(long userId);

    /// <summary>
    /// Same as <see cref="GetLanguageAsync"/> for several users at once, one query instead of N.
    /// Every requested id is present in the result.
    /// </summary>
    Task<Dictionary<long, string>> GetLanguagesAsync(IReadOnlyCollection<long> userIds);
}
