// <meshweaver>
// Id: Testing/DeploymentContract/ConfigurationBindingTest
// DisplayName: Testing/DeploymentContract/ConfigurationBindingTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using Microsoft.Extensions.Configuration;
using MeshWeaver.Deployment;

/// <summary>
/// The record reaches the portal as ONE configuration value, <c>Deployment:Record</c> — a JSON
/// document — whether the Aspire adapter injects it as the <c>Deployment__Record</c> environment
/// variable or the Helm chart renders it into the ConfigMap. The portal binds it at boot with
/// <see cref="DeploymentRecordJson.FromConfiguration"/>. This is the Orleans shape (resource →
/// environment → the app binds from configuration), and it is one value on purpose: init-only
/// records, immutable collections and the mesh's <c>$type</c> do not bind through the
/// configuration binder, and one JSON value is byte-identical on a ConfigMap and in Aspire.
/// </summary>
public class ConfigurationBindingTest
{
    [MeshFact]
    public void TheEnvironmentKeyIsTheConfigurationKeyInEnvironmentForm()
    {
        Assert.Equal("Deployment:Record", DeploymentRecordJson.ConfigurationKey);
        Assert.Equal(DeploymentRecordJson.EnvironmentKey, DeploymentRecordJson.ConfigurationKey.Replace(":", "__"));
    }

    [MeshFact]
    public void ARecordInjectedAsConfigurationBindsBackIdentically()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "memex.json");
        var record = DeploymentRecordJson.ReadFile(path);
        var injected = DeploymentRecordJson.Write(record);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DeploymentRecordJson.ConfigurationKey] = injected })
            .Build();

        var bound = DeploymentRecordJson.FromConfiguration(configuration);

        Assert.NotNull(bound);
        Assert.Equal(injected, DeploymentRecordJson.Write(bound!));
        Assert.Equal(record.Host, bound!.Host);
        Assert.Equal(record.PluginRepos.Count, bound.PluginRepos.Count);
        Assert.True(bound.PluginRepos.Count > 0, "the memex record declares plugin repositories — a zero here means the fixture changed, not that the binding works");
    }

    [MeshFact]
    public void AnAbsentSectionBindsToNoRecord()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Null(DeploymentRecordJson.FromConfiguration(configuration));
    }
}
