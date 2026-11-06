using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The deployment KIND and the host joins (Plugins Hosting/MacStudioFleet §2): a <c>mac-host</c>
/// record reads <c>kind</c>, <c>machine</c>, <c>networkName</c>, <c>registryInstanceId</c> and
/// <c>instances</c> from its JSON and writes them back unchanged; a record that states none of them —
/// every record before these fields — reads them as null and writes no key, so it renders exactly
/// what it rendered before; and the fields are init properties, so the record's constructor
/// signature is unchanged.
/// </summary>
public class MacHostRecordTest
{
    private const string MacHost = """
        {
          "$type": "DeploymentContent",
          "kind": "mac-host",
          "owner": "Systemorph",
          "machine": "C02XK0AAJGH7",
          "networkName": "studio-1",
          "registryInstanceId": "studio-1-control",
          "instances": ["studio-1-control", "studio-1-client-a"]
        }
        """;

    private const string AksInstance = """
        {
          "$type": "DeploymentContent",
          "host": "portal.example.com",
          "namespace": "portal",
          "cluster": "aks-fleet"
        }
        """;

    [Fact]
    public void AMacHostRecord_ReadsItsKindAndJoins_AndWritesThemBack()
    {
        var record = DeploymentRecordJson.Read(MacHost)!;
        Assert.Equal(DeploymentContent.MacHostKind, record.Kind);
        Assert.True(record.IsMacHost);
        Assert.Equal("C02XK0AAJGH7", record.Machine);
        Assert.Equal("studio-1", record.NetworkName);
        Assert.Equal("studio-1-control", record.RegistryInstanceId);
        Assert.Equal(new[] { "studio-1-control", "studio-1-client-a" }, record.Instances);

        var written = DeploymentRecordJson.Write(record);
        Assert.Contains("\"kind\":\"mac-host\"", written);
        Assert.Contains("\"machine\":\"C02XK0AAJGH7\"", written);
        Assert.Contains("\"networkName\":\"studio-1\"", written);
        Assert.Contains("\"instances\":[\"studio-1-control\",\"studio-1-client-a\"]", written);
        // The derivation is never a field at rest.
        Assert.DoesNotContain("isMacHost", written, StringComparison.OrdinalIgnoreCase);

        var again = DeploymentRecordJson.Read(written)!;
        Assert.Equal(record.Kind, again.Kind);
        Assert.Equal(record.Machine, again.Machine);
        Assert.Equal(record.NetworkName, again.NetworkName);
        Assert.Equal(record.RegistryInstanceId, again.RegistryInstanceId);
        Assert.Equal(record.Instances, again.Instances);
    }

    [Fact]
    public void ARecordStatingNoKind_IsAnAksInstance_ReadsNull_AndWritesNoKey()
    {
        var record = DeploymentRecordJson.Read(AksInstance)!;
        Assert.Null(record.Kind);
        Assert.False(record.IsMacHost);
        Assert.Null(record.Machine);
        Assert.Null(record.NetworkName);
        Assert.Null(record.Instances);

        var written = DeploymentRecordJson.Write(record);
        foreach (var key in new[] { "\"kind\"", "\"machine\"", "\"networkName\"", "\"instances\"", "isMacHost" })
            Assert.DoesNotContain(key, written, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(written, DeploymentRecordJson.Write(DeploymentRecordJson.Read(written)!));
    }

    [Theory]
    [InlineData("mac-host", true)]
    [InlineData(" MAC-HOST ", true)]
    [InlineData("aks", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMacHost_ReadsTheKind_TrimmedAndCaseInsensitive(string? kind, bool expected) =>
        Assert.Equal(expected, new DeploymentContent().WithKind(kind).IsMacHost);

    [Fact]
    public void AsMacHost_SetsTheKindAndJoins_AndAnOmittedJoinKeepsTheDeclaredOne()
    {
        var host = new DeploymentContent().AsMacHost("C02XK0AAJGH7", "studio-1", "studio-1-control");
        Assert.True(host.IsMacHost);
        Assert.Equal("C02XK0AAJGH7", host.Machine);
        Assert.Equal("studio-1", host.NetworkName);
        Assert.Equal(ImmutableList.Create("studio-1-control"), host.Instances);

        var renamed = host.AsMacHost("C02XK0AAJGH7", "studio-1b");
        Assert.Equal("studio-1b", renamed.NetworkName);
        Assert.Equal(host.Instances, renamed.Instances);

        var again = renamed.AsMacHost("C02XK0AAJGH7");
        Assert.Equal("studio-1b", again.NetworkName);
        Assert.Equal(host.Instances, again.Instances);

        var grown = again.AsMacHost("C02XK0AAJGH7", instances: "studio-1-client-a");
        Assert.Equal(new[] { "studio-1-control", "studio-1-client-a" }, grown.Instances);
        Assert.True(grown.WithKind(null).IsMacHost);
    }

    [Fact]
    public void TheJoinFieldsAreInitProperties_TheConstructorSignatureIsUnchanged()
    {
        // A primary-constructor parameter would replace the signature and break every module
        // compiled against the record; the public parameterless constructor is the contract.
        Assert.NotNull(typeof(DeploymentContent).GetConstructor(Type.EmptyTypes));
        foreach (var name in new[] { nameof(DeploymentContent.Kind), nameof(DeploymentContent.Machine), nameof(DeploymentContent.NetworkName), nameof(DeploymentContent.Instances) })
        {
            var setter = typeof(DeploymentContent).GetProperty(name)!.SetMethod!;
            Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }
    }
}
