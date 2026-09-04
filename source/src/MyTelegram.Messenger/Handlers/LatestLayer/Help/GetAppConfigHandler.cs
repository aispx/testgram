using MyTelegram.Messenger.Services.Emoji;
using MyTelegram.Messenger.Services.Transcription;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Help;
/// <summary>
/// Get app-specific configuration, see <a href="https://corefork.telegram.org/api/config#client-configuration">client configuration</a> for more info on the result.
/// <para><c>See <a href="https://corefork.telegram.org/method/help.getAppConfig"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✔]
/// </remarks>
internal sealed class GetAppConfigHandler(
    IAppConfigHelper appConfigHelper,
    IEmojiSoundAppConfigBuilder emojiSoundAppConfigBuilder,
    ITranscriptionAppConfigBuilder transcriptionAppConfigBuilder)
    : RpcResultObjectHandler<Schema.Help.RequestGetAppConfig, Schema.Help.IAppConfig>
{
    protected override async Task<Schema.Help.IAppConfig> HandleCoreAsync(IRequestInput input,
        Schema.Help.RequestGetAppConfig obj)
    {
        var config = appConfigHelper.GetAppConfig();
        var hash = appConfigHelper.GetAppConfigHash();

        // emojies_sounds is the one key that cannot be part of the shared configuration: it carries
        // per-session document access hashes, so it is built for this caller and folded into the hash -
        // a client that re-logs in must not be told notModified while holding hashes minted for the
        // previous authorization. See https://corefork.telegram.org/api/animated-emojis#emojis-with-sounds
        var sounds = await emojiSoundAppConfigBuilder.BuildAsync(input);
        if (sounds != null && config is TJsonObject jsonObject)
        {
            // A new object: the helper hands out one shared instance to every caller, and mutating it
            // would leak this session's access hashes into everybody else's configuration.
            config = new TJsonObject
            {
                Value = new TVector<IJSONObjectValue>(jsonObject.Value.Append(sounds.Value))
            };

            hash = unchecked(hash * 31 + sounds.Hash);
        }

        // transcribe_audio_trial_cooldown_until is per account for the same reason, and appears only while
        // that account's free transcription quota is actually spent - so the hash does not move for the
        // callers who have tries left. See https://corefork.telegram.org/api/transcribe
        var transcription = await transcriptionAppConfigBuilder.BuildAsync(input.UserId);
        if (transcription != null && config is TJsonObject configObject)
        {
            config = new TJsonObject
            {
                Value = new TVector<IJSONObjectValue>(configObject.Value.Append(transcription.Value))
            };

            hash = unchecked(hash * 31 + transcription.Hash);
        }

        if (obj.Hash == hash)
        {
            return new TAppConfigNotModified();
        }

        return new TAppConfig
        {
            Config = config,
            Hash = hash
        };
    }
}
