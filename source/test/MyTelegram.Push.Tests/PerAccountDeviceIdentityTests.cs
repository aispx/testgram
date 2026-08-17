using MyTelegram.Domain.Aggregates.PushDevice;
using Shouldly;

namespace MyTelegram.Push.Tests;

/// <summary>
/// The push device identity is scoped to (token, account). This is what makes multi-account push safe:
/// every account signed in on a device shares one push token but gets its own device row, so the
/// dispatcher routes purely by owner and no account can register a token on another account's behalf.
/// </summary>
public class PerAccountDeviceIdentityTests
{
    private const string Token = "shared-device-token";

    [Fact]
    public void Two_accounts_sharing_a_token_get_distinct_device_identities()
    {
        var accountA = PushDeviceId.Create(Token, 111);
        var accountB = PushDeviceId.Create(Token, 222);

        accountA.ShouldNotBe(accountB);
    }

    [Fact]
    public void Same_account_and_token_is_stable()
    {
        PushDeviceId.Create(Token, 111).ShouldBe(PushDeviceId.Create(Token, 111));
    }

    [Fact]
    public void Per_account_identity_differs_from_the_legacy_per_token_identity()
    {
        // A new registration targets the per-account id; the legacy id is only used to unregister the
        // pre-migration row. They must not collide, or the migration cleanup would delete the new row.
        PushDeviceId.Create(Token, 111).ShouldNotBe(PushDeviceId.CreateLegacy(Token));
    }
}
