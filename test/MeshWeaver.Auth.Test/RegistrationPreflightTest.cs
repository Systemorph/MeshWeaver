using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the auto-registration PRE-FLIGHT (#2585): every branch that decides whether an
/// installation may present its bootstrap key, most importantly the master-key probe whose ORDER
/// is the fix — an installation that cannot encrypt must refuse BEFORE the registry issues the
/// one-time <c>mwi_</c> key, because registering first and failing to store burned both the key
/// and the instance id (measured 2026-08-28 on a fresh harness mesh: the registry answered 409
/// "already registered" forever after, while the pod held nothing).
/// </summary>
public class RegistrationPreflightTest
{
    private const string Key = "mwr_test";

    [Fact]
    public void NoBootstrapKey_IsANoOp()
    {
        var (action, reason) = InstanceAutoRegistrationService.DecideRegistration(
            null, null, null, masterKeyPresent: false, consentGiven: true);
        Assert.Equal(InstanceAutoRegistrationService.RegistrationPreflight.Skip, action);
        Assert.Null(reason);
    }

    [Fact]
    public void ExplicitToken_WinsOverTheBootstrapKey()
    {
        var (action, reason) = InstanceAutoRegistrationService.DecideRegistration(
            Key, "mwi_configured", "my-instance", masterKeyPresent: true, consentGiven: true);
        Assert.Equal(InstanceAutoRegistrationService.RegistrationPreflight.Skip, action);
        Assert.Contains("explicit token wins", reason);
    }

    [Fact]
    public void MissingInstanceId_Aborts_NamingTheSetting()
    {
        var (action, reason) = InstanceAutoRegistrationService.DecideRegistration(
            Key, null, "  ", masterKeyPresent: true, consentGiven: true);
        Assert.Equal(InstanceAutoRegistrationService.RegistrationPreflight.Abort, action);
        Assert.Contains("PluginCatalog:InstanceId", reason);
    }

    [Fact]
    public void NoMasterKey_Aborts_BeforeAnythingIsConsumed()
    {
        // 🚨 THE #2585 REGRESSION PIN. Without a master key the issued key could not be stored
        // (encrypted-or-not-at-all), so the pre-flight must refuse — and must SAY that nothing
        // was consumed, because the whole point of refusing early is that the bootstrap key stays
        // valid and the instance id stays free.
        var (action, reason) = InstanceAutoRegistrationService.DecideRegistration(
            Key, null, "my-instance", masterKeyPresent: false, consentGiven: true);
        Assert.Equal(InstanceAutoRegistrationService.RegistrationPreflight.Abort, action);
        Assert.Contains("Ai:KeyProtection:MasterKey", reason);
        Assert.Contains("before anything is consumed", reason);
    }

    [Fact]
    public void FullyConfigured_Registers()
    {
        var (action, reason) = InstanceAutoRegistrationService.DecideRegistration(
            Key, "", "my-instance", masterKeyPresent: true, consentGiven: true);
        Assert.Equal(InstanceAutoRegistrationService.RegistrationPreflight.Register, action);
        Assert.Null(reason);
    }
}
