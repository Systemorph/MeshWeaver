#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    /// <summary>
    /// 🚨 The test above measured the WRONG PATH, and was green for it while every deployed portal
    /// was broken (#2517, #2507). It covers <c>InstallConfiguredModules</c> — one helper, used by
    /// the hosts simple enough to read <c>Modules:Assemblies</c> and install it. The PORTAL does not
    /// use it: it composes its own module union (appsettings baseline + activation sidecar +
    /// platform floor + generation resolution) and calls <see cref="MeshBuilder.InstallAssemblies"/>
    /// directly — so it never reached the hand-off, and every module contribution ran against a
    /// <c>null</c> configuration.
    ///
    /// <para>That is not a portal bug to fix in the portal. <c>InstallAssemblies</c> is where module
    /// contributions RUN, it takes no configuration, and any composition root may call it — so
    /// "remember to hand the configuration over first" is a rule each new composer has to
    /// rediscover, and one of them already did not. A host that binds a mesh to an
    /// <see cref="IHostApplicationBuilder"/> always HAS the configuration, so
    /// <c>MeshHostApplicationBuilder</c> takes it at construction and no caller can be too late.</para>
    ///
    /// <para>This test installs through <c>InstallAssemblies</c> — deliberately NOT through the
    /// helper — because that is the call the portal makes.</para>
    /// </summary>
    [Fact]
    public void AHostBoundBuilder_ExposesTheHostsConfiguration_ToAModuleInstalledDirectly()
    {
        var host = Host.CreateApplicationBuilder();
        var builder = new MeshHostApplicationBuilder(host, AddressExtensions.CreateMeshAddress());

        // The portal's call — the module union it computed itself, straight to InstallAssemblies.
        builder.InstallAssemblies(typeof(ModuleConfigurationVisibilityTest).Assembly.Location);

        // Read the registration rather than building a provider: the value the module captured is
        // registered as a singleton INSTANCE, so the descriptor already holds the answer, and this
        // cannot fail for reasons unrelated to the claim.
        var captured = host.Services
            .LastOrDefault(d => d.ServiceType == typeof(CapturedConfiguration))?
            .ImplementationInstance as CapturedConfiguration;

        captured.Should().NotBeNull(
            "the module's BuilderConfigurations must have run — otherwise this test proves nothing "
            + "about what it saw and must fail rather than pass vacuously");
        captured!.Value.Should().BeSameAs(host.Configuration,
            "a module installed by ANY composition root must see the deployment's configuration. "
            + "When it does not, the failure is silent and total: the AI engine reads exactly this "
            + "property to decide whether Agent/Skill/Harness/Model/Provider are served from the "
            + "database, a null answers 'in-memory', and each in-memory type-definition node then "
            + "shadows the durable package root at its own top-level path — /Agent and /Skill "
            + "served the built-in NodeType instead of the authored Store page (#2517) and the "
            + "static-repo import never registered (#2507)");
    }
}
