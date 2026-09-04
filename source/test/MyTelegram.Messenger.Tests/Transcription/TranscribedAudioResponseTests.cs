using System.Buffers;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Messenger.Services.Transcription;
using MyTelegram.Schema;
using MyTelegram.Schema.Extensions;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Tests.Transcription;

/// <summary>
/// Feature: the response of <a href="https://corefork.telegram.org/api/transcribe">messages.transcribeAudio</a>
/// and the appConfig numbers clients bound it with.
///
/// <para>
/// <c>trial_remains_num</c> and <c>trial_remains_until_date</c> share <b>flag bit 1</b>
/// (<c>messages.transcribedAudio#cfb9d957 … trial_remains_num:flags.1?int
/// trial_remains_until_date:flags.1?int</c>), and the generated serializer transcribes that faithfully:
/// raising the bit for one dereferences the other. Setting only one therefore throws while the response is
/// being written — past the point where a handler can fail cleanly, so the caller is never answered at all
/// and the log shows a successful handler followed by a serializer stack trace. The same trap
/// <c>wallPaperSettings</c> fell into.
/// </para>
/// </summary>
public class TranscribedAudioResponseTests
{
    private static byte[] Serialize(TTranscribedAudio audio)
    {
        var writer = new ArrayBufferWriter<byte>();
        audio.Serialize(writer);

        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void A_trial_response_carries_both_fields_and_round_trips()
    {
        var audio = new TTranscribedAudio
        {
            Pending = true,
            TranscriptionId = 7_314_159_265_358_979,
            Text = string.Empty,
            TrialRemainsNum = 2,
            TrialRemainsUntilDate = 1_800_000_000
        };

        var bytes = Serialize(audio);

        var buffer = new ReadOnlyMemory<byte>(bytes)[4..];
        var parsed = new TTranscribedAudio();
        parsed.Deserialize(ref buffer);

        parsed.Pending.ShouldBeTrue();
        parsed.TranscriptionId.ShouldBe(audio.TranscriptionId);
        parsed.TrialRemainsNum.ShouldBe(2);
        parsed.TrialRemainsUntilDate.ShouldBe(1_800_000_000);
    }

    /// <summary>
    /// A Premium (or boosted-supergroup) caller gets neither field, and bit 1 must stay clear — an
    /// unlimited caller told "0 tries left" is exactly the state Android renders as an exhausted quota.
    /// </summary>
    [Fact]
    public void An_unlimited_response_carries_neither_field()
    {
        var audio = new TTranscribedAudio
        {
            Pending = true,
            TranscriptionId = 42,
            Text = string.Empty
        };

        Should.NotThrow(() => Serialize(audio));
        audio.Flags.IsBitSet(1).ShouldBeFalse();
    }

    /// <summary>
    /// The trap itself, pinned so nobody "simplifies" the handler into setting one field: this is what a
    /// half-filled pair does, and it happens while the answer is being written.
    /// </summary>
    [Fact]
    public void One_trial_field_without_the_other_cannot_be_serialized()
    {
        var audio = new TTranscribedAudio
        {
            TranscriptionId = 42,
            Text = string.Empty,
            TrialRemainsNum = 2
        };

        Should.Throw<InvalidOperationException>(() => Serialize(audio));
    }

    /// <summary>
    /// The trial has to exist in the advertised configuration, or it exists nowhere: tdesktop enables the
    /// whole trial UI only for <c>weekly_number > 0 || cooldown_until > 0</c>
    /// (<c>Transcribes::trialsSupport()</c>), and with a weekly number of 0 a non-Premium user is never
    /// offered the button at all.
    /// </summary>
    [Fact]
    public void AppConfig_advertises_a_non_zero_weekly_trial()
    {
        ConfigNumber("transcribe_audio_trial_weekly_number").ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The duration ceiling and the supergroup exemption both come from appConfig, because the server has
    /// to refuse at the number the client was told rather than one of its own.
    /// </summary>
    [Fact]
    public void AppConfig_advertises_the_numbers_the_server_enforces()
    {
        ConfigNumber("transcribe_audio_trial_duration_max")
            .ShouldBe(TranscriptionLimits.TrialDurationFallback);

        ConfigNumber("group_transcribe_level_min")
            .ShouldBe(TranscriptionLimits.GroupFreeLevelFallback);
    }

    /// <summary>
    /// <c>transcribe_audio_trial_cooldown_until</c> is per account and is added by
    /// <c>GetAppConfigHandler</c>, so it must not be in the shared table — one constant there would tell
    /// every caller the same cooldown.
    /// </summary>
    [Fact]
    public void The_shared_config_carries_no_per_account_cooldown()
    {
        ConfigValue(TranscriptionAppConfigBuilder.ConfigKey).ShouldBeNull();
    }

    private static double ConfigNumber(string key)
    {
        return ((TJsonNumber)ConfigValue(key)!).Value;
    }

    private static IJSONValue? ConfigValue(string key)
    {
        return ((TJsonObject)new AppConfigHelper().GetAppConfig()).Value
            .OfType<TJsonObjectValue>()
            .FirstOrDefault(p => p.Key == key)
            ?.Value;
    }
}
