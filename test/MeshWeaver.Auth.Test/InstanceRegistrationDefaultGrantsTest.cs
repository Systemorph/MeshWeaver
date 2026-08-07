using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins the <c>PluginCatalog:DefaultGrants</c> seed of instance registration: a registry operator
/// may opt sources into every NEW registration (e.g. <c>Plugins/*</c>, so a fresh install gets the
/// platform plugin repo with no admin step), and registration then writes those entries into the
/// instance's <c>Admin/_PluginGrant</c> node. The grant node stays the single authority — an admin
/// can still revoke per instance — and with NO defaults configured, registration keeps granting
/// exactly nothing ("identity, not entitlement", the strict default).
/// </summary>
public class InstanceRegistrationDefaultGrantsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private MeshWeaverInstanceService Service(params string[] defaultGrants)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(defaultGrants.Select((entry, i) => new KeyValuePair<string, string?>(
                $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
            .Build();
        return new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            configuration);
    }

    /// <summary>Reads the grant node as System — the grant lives in the Admin partition, which is
    /// exactly why the seed itself must run as System (the registering user cannot reach it).</summary>
    private IObservable<MeshNode?> ReadGrantNode(string instanceId)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => Mesh.GetMeshNode(
                    MeshWeaverInstanceNodeType.GrantPath(instanceId), TimeSpan.FromSeconds(10)))
            .Take(1);
    }

    [Fact]
    public async Task Register_WithConfiguredDefaults_SeedsTheGrantNode()
    {
        var service = Service("Plugins/*", "Reinsurance/UWDeepfield");

        var registration = await service.Register(
                "user-defaults", "Default Grants", "defaults@test.com",
                "dg-seeded-instance", "Seeded instance")
            .Should().Emit();

        var grantNode = await ReadGrantNode("dg-seeded-instance").Should().Emit();
        grantNode.Should().NotBeNull("registration must seed the configured default grants");
        var grant = grantNode!.ContentAs<PluginGrant>(Mesh.JsonSerializerOptions)!;

        grant.InstanceId.Should().Be("dg-seeded-instance");
        // Policy wrote it, not a person — the attribution must say so.
        grant.GrantedByUserId.Should().Be(WellKnownUsers.System);
        grant.Allows("Plugins", "Store").Should().BeTrue("Plugins/* is a configured default");
        grant.Allows("Reinsurance", "UWDeepfield").Should().BeTrue("the single-package default");
        grant.Allows("Reinsurance", "ClaimsDeepfield").Should().BeFalse("only the granted package");
        grant.Allows("Education", "AgenticEngineering").Should().BeFalse(
            "sources outside the default list stay admin-granted");

        // End to end: the key issued at registration must resolve to a grant that ALLOWS the
        // defaults — the same path the registry's /api/plugins surface authenticates through.
        var authenticator = new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());
        var authenticated = await authenticator
            .Authenticate($"Bearer {registration.RawKey}")
            .Should().Emit();
        authenticated.Should().NotBeNull();
        authenticated!.Allows("Plugins", "Agent").Should().BeTrue(
            "a freshly registered instance must be able to pull the defaulted source immediately");
    }

    [Fact]
    public async Task Register_WithNoDefaultsConfigured_GrantsNothing()
    {
        var service = Service();

        await service.Register(
                "user-defaults", "Default Grants", "defaults@test.com",
                "dg-strict-instance", "Strict instance")
            .Should().Emit();

        // The strict default is preserved exactly: no grant node is written at all, so the
        // authenticator resolves the empty grant ("registering is identity, not entitlement").
        var grantNode = await ReadGrantNode("dg-strict-instance").Should().Emit();
        grantNode.Should().BeNull("no defaults configured → registration must not write a grant");
    }

    [Fact]
    public async Task Register_MalformedDefaultEntries_AreSkippedNotFatal()
    {
        // Operator-typed config: one bad entry must neither fail registration nor poison the list.
        var service = Service("  ", "/", "Plugins/*");

        await service.Register(
                "user-defaults", "Default Grants", "defaults@test.com",
                "dg-tolerant-instance", "Tolerant instance")
            .Should().Emit();

        var grantNode = await ReadGrantNode("dg-tolerant-instance").Should().Emit();
        grantNode.Should().NotBeNull();
        var grant = grantNode!.ContentAs<PluginGrant>(Mesh.JsonSerializerOptions)!;
        grant.Entries.Should().HaveCount(1);
        grant.Allows("Plugins", "Store").Should().BeTrue();
    }
}
