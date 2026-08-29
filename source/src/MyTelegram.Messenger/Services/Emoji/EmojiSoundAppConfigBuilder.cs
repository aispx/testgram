using System.Globalization;

namespace MyTelegram.Messenger.Services.Emoji;

/// <summary>
/// Builds the <c>emojies_sounds</c> entry of <c>help.getAppConfig</c>:
/// <a href="https://corefork.telegram.org/api/animated-emojis#emojis-with-sounds">the soundbites</a>
/// clients play when an animated emoji is clicked.
///
/// <para><b>The entry is per session and therefore cannot live in the static configuration.</b> The map
/// carries the <c>access_hash</c> a client quotes back in <c>upload.getFile</c>, and document access
/// hashes here are minted from the caller's <c>AccessHashKeyId</c>
/// (<see cref="MyTelegram.Services.Services.AccessHashHelper2"/>), which the deployed file-server
/// validates before the request reaches this repository. Real Telegram can publish one constant per
/// document; this server cannot, so the entry is rebuilt for every caller and the config hash moves
/// with it.</para>
///
/// <para><b>All three fields must be strings.</b> tdlib's <c>ConfigManager</c> skips any member of a
/// sound object that is not <c>jsonString</c> and then rejects the whole entry for a missing id;
/// Android (<c>MessagesController.applyAppConfig</c>) casts to <c>TL_jsonString</c> the same way. A
/// numeric <c>id</c> is silently dropped by every client.</para>
///
/// <para>The file reference is unpadded base64url, which is what <c>is_base64url</c> accepts and what
/// <c>Base64.decode(fr, Base64.URL_SAFE)</c> expects. Telegram itself currently serves an empty string
/// there; sending the real reference is strictly more information and costs nothing.</para>
/// </summary>
public interface IEmojiSoundAppConfigBuilder
{
    /// <summary>
    /// The <c>emojies_sounds</c> entry for this caller, or <c>null</c> when nothing is seeded - the key
    /// is then left out entirely rather than served empty, exactly as Telegram omits keys it has no
    /// value for.
    /// </summary>
    Task<EmojiSoundAppConfigEntry?> BuildAsync(IRequestWithAccessHashKeyId input,
        CancellationToken cancellationToken = default);
}

/// <param name="Value">The <c>emojies_sounds</c> key/value pair to add to the configuration object.</param>
/// <param name="Hash">
/// A hash of everything in <paramref name="Value"/>, to be mixed into the configuration hash so a
/// client whose cached copy holds different access hashes is not answered <c>appConfigNotModified</c>.
/// </param>
public sealed record EmojiSoundAppConfigEntry(TJsonObjectValue Value, int Hash);

/// <inheritdoc />
public class EmojiSoundAppConfigBuilder(
    IEmojiSoundStore store,
    IAccessHashHelper2 accessHashHelper,
    IFileReferenceHelper fileReferenceHelper)
    : IEmojiSoundAppConfigBuilder, ITransientDependency
{
    public const string ConfigKey = "emojies_sounds";

    public async Task<EmojiSoundAppConfigEntry?> BuildAsync(IRequestWithAccessHashKeyId input,
        CancellationToken cancellationToken = default)
    {
        var sounds = await store.GetAllAsync(cancellationToken);
        if (sounds.Count == 0)
        {
            return null;
        }

        var values = new List<IJSONObjectValue>(sounds.Count);
        var hash = 2166136261u;

        void Mix(string value)
        {
            unchecked
            {
                foreach (var c in value)
                {
                    hash = (hash ^ c) * 16777619u;
                }

                hash = (hash ^ 0x1Fu) * 16777619u; // field separator
            }
        }

        foreach (var sound in sounds)
        {
            var accessHash = accessHashHelper.GenerateAccessHash(input.UserId, input.AccessHashKeyId,
                sound.DocumentId, AccessHashType.Document);
            var id = sound.DocumentId.ToString(CultureInfo.InvariantCulture);
            var accessHashText = accessHash.ToString(CultureInfo.InvariantCulture);
            // Minted, not read from the row: this value is a real file reference that clients quote back
            // in upload.getFile, and the stored one no longer means anything. It has to come from the same
            // helper as every other reference or the download is refused.
            // See https://corefork.telegram.org/api/file-references
            var fileReference = ToBase64Url(
                fileReferenceHelper.Create(AccessHashType.Document, sound.DocumentId));

            values.Add(new TJsonObjectValue
            {
                Key = sound.Emoticon,
                Value = new TJsonObject
                {
                    Value =
                    [
                        new TJsonObjectValue { Key = "id", Value = new TJsonString { Value = id } },
                        new TJsonObjectValue
                        {
                            Key = "access_hash",
                            Value = new TJsonString { Value = accessHashText }
                        },
                        new TJsonObjectValue
                        {
                            Key = "file_reference_base64",
                            Value = new TJsonString { Value = fileReference }
                        }
                    ]
                }
            });

            Mix(sound.Emoticon);
            Mix(id);
            Mix(accessHashText);
            Mix(fileReference);
        }

        var entry = new TJsonObjectValue
        {
            Key = ConfigKey,
            Value = new TJsonObject { Value = new TVector<IJSONObjectValue>(values) }
        };

        return new EmojiSoundAppConfigEntry(entry, unchecked((int)hash));
    }

    /// <summary>
    /// Unpadded base64url. <c>is_base64url</c> rejects a padded string of some lengths and every client
    /// decodes with the URL-safe alphabet, so <c>+</c>/<c>/</c>/<c>=</c> must not appear.
    /// </summary>
    public static string ToBase64Url(byte[] value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
