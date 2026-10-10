using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// Refs #4305: approval step-up is DECLARED on the deployment record (<c>signIn.stepUp</c>) and
/// rendered to the portal's <c>Authentication__StepUp__*</c> keys — and a record that does not
/// declare it renders NOTHING, so the portal's default (off) applies and no instance changes
/// behaviour before its tenant admin has created the Conditional Access context.
/// </summary>
public class StepUpRecordTest
{
    private const string Declared = """
        {
          "$type": "DeploymentContent",
          "host": "memex.example.com",
          "signIn": {
            "provider": "Custom",
            "microsoftClientId": "client",
            "microsoftTenantId": "tenant-guid"
          },
          "approvalStepUp": { "enabled": true, "entraAuthenticationContext": "c1", "maxAuthAgeSeconds": 90 }
        }
        """;

    private const string Undeclared = """
        {
          "$type": "DeploymentContent",
          "host": "memex.example.com",
          "signIn": { "provider": "Custom", "microsoftClientId": "client" }
        }
        """;

    [Fact]
    public void ADeclaredStepUpRendersItsKeys()
    {
        var record = DeploymentRecordJson.Read(Declared)!;
        Assert.Equal("c1", record.ApprovalStepUp!.EntraAuthenticationContext);

        var config = DeploymentPortalConfig.PortalConfig(record);
        Assert.Equal("true", config["Authentication__StepUp__Enabled"]);
        Assert.Equal("c1", config["Authentication__StepUp__Entra__AuthenticationContext"]);
        Assert.Equal("90", config["Authentication__StepUp__MaxAuthAgeSeconds"]);
        // Unstated fields render nothing — the portal default applies.
        Assert.False(config.ContainsKey("Authentication__StepUp__Entra__RequireAmr"));
        Assert.False(config.ContainsKey("Authentication__StepUp__ReceiptLifetimeSeconds"));
    }

    [Fact]
    public void ARecordWithoutStepUpRendersNoStepUpKey()
    {
        var config = DeploymentPortalConfig.PortalConfig(DeploymentRecordJson.Read(Undeclared)!);
        Assert.DoesNotContain(config.Keys, k => k.StartsWith("Authentication__StepUp__", StringComparison.Ordinal));
        // Negative control: the same renderer does render the rest of the sign-in section.
        Assert.Equal("client", config["Authentication__Microsoft__ClientId"]);
    }
}
