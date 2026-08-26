#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: MeshWeaver.Hosting.Monolith.Test.ConfigurationVisibilityModule]

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>What the module saw, carried out through the service collection rather than a static.</summary>
public sealed record CapturedConfiguration(IConfiguration? Value);

/// <summary>
/// A module that answers one question: was the deployment's configuration already on the builder
/// when my contribution ran? It records the answer as a service, so the test reads it the same way
/// production would — no static capture, nothing to reset between tests.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ConfigurationVisibilityModuleAttribute : MeshNodeProviderAttribute
{
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
    [
        builder =>
        {
            // 🚨 Read it HERE — at INSTALL time, inside the contribution itself. Reading it inside
            // the ConfigureServices lambda instead would defer the read to whenever the collected
            // service delegates are applied, so the test would pass even if the hand-off happened
            // AFTER installation — pinning nothing about ordering, which is the whole claim.
            var seenAtInstallTime = builder.Configuration;
            return builder.ConfigureServices(services =>
                services.AddSingleton(new CapturedConfiguration(seenAtInstallTime)));
        },
    ];
}

/// <summary>
/// A module contributes at INSTALL time, and some of what it must decide is a config value that
/// cannot wait for DI. `MeshWeaver.Social` records the gap — "there is no IConfiguration instance
/// at install time" — and binds through the options pipeline instead. Options answer at RESOLVE
/// time, which is fine for a typed HTTP client and useless for a decision that BUILDS something:
/// whether a type-definition node is <c>IsDefinitionOnly</c> is an <c>init</c> property fixed when
/// the node is constructed, and getting it wrong leaves a partition root permanently unrecoverable
/// (#902). That is why the AI engine's <c>AddAI(serveFromPartition)</c> could not ride a module
/// attribute (#2276).
///
/// <para>The configuration was never far away: <c>InstallConfiguredModules</c> already TAKES an
/// <see cref="IConfiguration"/> — it reads <c>Modules:Assemblies</c> out of it — and then dropped
/// it before installing. Now it hands it to the builder first.</para>
/// </summary>
public class ModuleConfigurationVisibilityTest
{
    [Fact]
    public void ABareBuilder_HasNoConfiguration()
        => new MeshBuilder(_ => { }, AddressExtensions.CreateMeshAddress())
            .Configuration
            .Should().BeNull(
                "a bespoke host or fixture supplies none, and a module reading it must treat null "
                + "as 'not configured' rather than guessing");

    [Fact]
    public void InstallingConfiguredModules_ExposesTheConfigurationTOTheModule_BeforeItRuns()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure),
            AddressExtensions.CreateMeshAddress());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // This assembly — its DLL sits in the test's base directory, so ResolveModulePath
                // finds it exactly the way a deployment's module entry resolves.
                ["Modules:Assemblies:0"] = "MeshWeaver.Hosting.Monolith.Test.dll",
            })
            .Build();

        builder.InstallConfiguredModules(configuration);

        var services = new ServiceCollection();
        foreach (var configure in serviceConfigs)
            configure(services);
        var captured = services.BuildServiceProvider().GetService<CapturedConfiguration>();

        captured.Should().NotBeNull(
            "the module's BuilderConfigurations must have run — if it did not, this test proves "
            + "nothing about ordering and must fail rather than pass vacuously");
        captured!.Value.Should().BeSameAs(configuration,
            "the module has to see the deployment's OWN configuration, and see it BEFORE its "
            + "contribution runs — a value that arrived afterwards is a value the module could not "
            + "have used to decide what to build");
    }
}
