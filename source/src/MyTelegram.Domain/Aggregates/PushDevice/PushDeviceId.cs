namespace MyTelegram.Domain.Aggregates.PushDevice;

[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<PushDeviceId>))]
public class PushDeviceId(string value) : Identity<PushDeviceId>(value)
{
    /// <summary>
    /// One registration per (token, account). A push token is shared by every account signed in on a
    /// device, so scoping the identity by the authenticated user id gives each account its own row and
    /// the dispatcher routes purely by owner (<c>UserId == recipient</c>). This is what makes
    /// multi-account push safe without trusting a caller-supplied <c>other_uids</c> list: an account
    /// only ever receives pushes for a token it registered itself from an authenticated session.
    /// </summary>
    public static PushDeviceId Create(string token, long userId)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands, $"pushdevice_{token}_{userId}");
    }

    /// <summary>
    /// Legacy per-token identity (before per-account scoping). Kept only so a new registration can
    /// unregister the pre-migration row for its token, otherwise its last owner would keep receiving
    /// a duplicate of every push.
    /// </summary>
    public static PushDeviceId CreateLegacy(string token)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands, $"pushdevice_{token}");
    }
}
