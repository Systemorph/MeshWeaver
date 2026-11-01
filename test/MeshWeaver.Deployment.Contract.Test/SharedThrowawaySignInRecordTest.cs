using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// Refs Systemorph/MeshWeaver.Plugins#4336: a throwaway or test instance opts into the fleet's
/// SHARED sign-in app with <c>signIn.sharedThrowawayApp</c>. The record READS it (the control
/// instance's Provision and Teardown plan on it), and the portal renderer emits NO key for it — the
/// portal signs in through <c>Authentication__Microsoft__ClientId</c> as it does for its own app, and
/// a key the chart does not template would reach no container (config-key-coverage).
/// </summary>
public class SharedThrowawaySignInRecordTest
{
    private const string Shared = """
        {
          "$type": "DeploymentContent",
          "host": "proof.example.com",
          "signIn": {
            "provider": "Custom",
            "microsoftClientId": "shared-app",
            "sharedThrowawayApp": true
          }
        }
        """;

    private const string Own = """
        {
          "$type": "DeploymentContent",
          "host": "proof.example.com",
          "signIn": { "provider": "Custom", "microsoftClientId": "own-app" }
        }
        """;

    [Fact]
    public void TheFlagIsReadFromTheRecord()
    {
        Assert.True(DeploymentRecordJson.Read(Shared)!.SignIn!.SharedThrowawayApp);
        // Negative control: a record that does not state it is an instance with its OWN app.
        Assert.False(DeploymentRecordJson.Read(Own)!.SignIn!.SharedThrowawayApp);
    }

    [Fact]
    public void TheFlagRendersNoPortalKey()
    {
        var shared = DeploymentPortalConfig.PortalConfig(DeploymentRecordJson.Read(Shared)!);
        var own = DeploymentPortalConfig.PortalConfig(DeploymentRecordJson.Read(Own)!);
        Assert.Equal("shared-app", shared["Authentication__Microsoft__ClientId"]);
        // The flag changes nothing the portal reads: the same key set, only the client id differs.
        Assert.Equal(
            own.Keys.OrderBy(k => k, StringComparer.Ordinal),
            shared.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("own-app", own["Authentication__Microsoft__ClientId"]);
    }
}
