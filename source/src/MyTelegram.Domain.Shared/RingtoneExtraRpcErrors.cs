namespace MyTelegram;

/// <summary>
/// Hand-written RPC errors for <a href="https://corefork.telegram.org/api/ringtones">notification
/// sounds »</a> that are not present in the generated <see cref="RpcErrors"/> (<c>RpcErrors.g.cs</c>).
/// Do not add these to the generated file; it is regenerated and would lose manual edits.
///
/// <para>Neither string is documented in the "Possible errors" table of
/// <c>account.uploadRingtone</c>, which lists only <c>RINGTONE_MIME_INVALID</c> — but Telegram Android
/// matches both of them literally and turns each into its own message
/// (<c>RingtoneUploader.error()</c>: <c>ErrorRingtoneDurationTooLong</c> /
/// <c>ErrorRingtoneSizeTooBig</c>, both formatted with the limit from appConfig). Answering anything
/// else there gives the user "an unknown error occurred" for a file that is merely too long.</para>
/// </summary>
public static class RingtoneExtraRpcErrors
{
    /// <summary>
    /// The uploaded sound is longer than
    /// <a href="https://corefork.telegram.org/api/config#ringtone-duration-max">ringtone_duration_max »</a>.
    /// <code>
    /// account.uploadRingtone
    /// </code>
    /// </summary>
    public static readonly RpcError RingtoneDurationTooLong = new(400, "RINGTONE_DURATION_TOO_LONG");

    /// <summary>
    /// The uploaded sound is larger than
    /// <a href="https://corefork.telegram.org/api/config#ringtone-size-max">ringtone_size_max »</a>.
    /// <code>
    /// account.uploadRingtone
    /// </code>
    /// </summary>
    public static readonly RpcError RingtoneSizeTooBig = new(400, "RINGTONE_SIZE_TOO_BIG");
}
