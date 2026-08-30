using Xunit;
using static MeshWeaver.PluginCatalog.InstanceAutoRegistrationService;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the registration pre-flight — the pure decision of whether a booting installation presents
/// itself to its registry, and with what. Two lanes, deliberately: a configured bootstrap key
/// (<c>mwr_…</c>, minted by a platform admin for a plan) and OPEN registration — no key at all, just
/// an instance id — which the registry accepts only when it has been configured with a plan for
/// un-keyed callers (the free tier on memex.meshweaver.cloud) and refuses with 401 otherwise.
///
/// <para>🚨 The open lane must NOT turn a deployment that merely SET an instance id into one that
/// aborts its install when the registry refuses: that id was harmless yesterday. A refused open
/// registration is logged and skipped (<c>EnsureRegistered</c>), never an abort — the abort
/// branches stay where a configured key contradicts the rest of the configuration.</para>
/// </summary>
public class RegistrationPreflightTest
{
    [Fact]
    public void NothingConfigured_SkipsSilently()
    {
        var (action, reason) = DecideRegistration(null, null, null, masterKeyPresent: true, consentGiven: true);
        Assert.Equal(RegistrationPreflight.Skip, action);
        Assert.Null(reason);
    }

    [Fact]
    public void AnInstanceIdWithoutAKey_RegistersOpenly()
        // The Homebrew default: `memex-local registry https://memex.meshweaver.cloud` with no key.
        // Whether the registry accepts it — and into which plan — is the REGISTRY's decision.
        => Assert.Equal(RegistrationPreflight.Register,
            DecideRegistration("", "", "roland-macbook", masterKeyPresent: true, consentGiven: true).Action);

    [Fact]
    public void OpenRegistration_WithoutAMasterKey_SkipsInsteadOfAborting()
    {
        // The issued instance key is stored encrypted or not at all (#2585). A keyed deployment
        // that cannot encrypt ABORTS (the key would be burned); an open one has nothing to burn, so
        // it skips — but says why, because a silent skip is a registration nobody can explain.
        var (action, reason) = DecideRegistration("", "", "roland-macbook", masterKeyPresent: false, consentGiven: true);
        Assert.Equal(RegistrationPreflight.Skip, action);
        Assert.Contains("MasterKey", reason);
    }

    [Fact]
    public void AnExplicitToken_WinsOverBothLanes()
    {
        Assert.Equal(RegistrationPreflight.Skip,
            DecideRegistration("mwr_key", "mwi_token", "id", masterKeyPresent: true, consentGiven: true).Action);
        Assert.Equal(RegistrationPreflight.Skip,
            DecideRegistration("", "mwi_token", "id", masterKeyPresent: true, consentGiven: true).Action);
    }

    [Fact]
    public void AKeyWithoutAnInstanceId_Aborts()
        => Assert.Equal(RegistrationPreflight.Abort,
            DecideRegistration("mwr_key", "", "", masterKeyPresent: true, consentGiven: true).Action);

    [Fact]
    public void AKeyWithoutAMasterKey_Aborts()
        // #2585 — registering first and failing to store would burn the key AND the id.
        => Assert.Equal(RegistrationPreflight.Abort,
            DecideRegistration("mwr_key", "", "id", masterKeyPresent: false, consentGiven: true).Action);

    [Fact]
    public void OpenRegistration_WithoutConsent_WaitsInsteadOfRegistering()
    {
        // The instance starts, and ASKS. Until a platform admin accepts the privacy statement and
        // the platform terms, an open registration neither registers nor aborts — it waits, and
        // says where the answer is given.
        var (action, reason) = DecideRegistration("", "", "roland-macbook", masterKeyPresent: true, consentGiven: false);
        Assert.Equal(RegistrationPreflight.AwaitConsent, action);
        Assert.Contains("Instance registration", reason);
    }

    [Fact]
    public void AKeyedRegistration_IsNotGatedOnConsent()
        // An operator who provisions with a registration key accepted the terms on the fleet's
        // side; an unattended pod asked the same question would stay unregistered forever.
        => Assert.Equal(RegistrationPreflight.Register,
            DecideRegistration("mwr_key", "", "id", masterKeyPresent: true, consentGiven: false).Action);

    [Fact]
    public void OpenRegistration_TheMasterKeyIsCheckedBeforeConsent()
        // Infrastructure before people: an admin who consents on an instance that cannot store the
        // key would be told AFTER consenting — so the config problem is reported first.
        => Assert.Equal(RegistrationPreflight.Skip,
            DecideRegistration("", "", "id", masterKeyPresent: false, consentGiven: false).Action);

    [Fact]
    public void AKeyAndAnInstanceId_Register()
        => Assert.Equal(RegistrationPreflight.Register,
            DecideRegistration("mwr_key", "", "id", masterKeyPresent: true, consentGiven: true).Action);
}
