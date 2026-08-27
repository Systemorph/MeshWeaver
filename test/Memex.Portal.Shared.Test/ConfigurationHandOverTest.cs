using Memex.Portal.Shared;
using Memex.Portal.Shared.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The probe module: this TEST assembly is itself a loadable module (ResolveModulePath passes an
// absolute path straight through), so pointing Modules:Assemblies at it makes the REAL fold —
// ConfigureMemexMesh → InstallAssemblies → attribute BuilderConfigurations — observable.
[assembly: ConfigurationProbeModule]

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #2507, pinned: the portal boot path must hand the host configuration to the builder BEFORE
/// modules install. A module attribute's <c>BuilderConfigurations</c> run inside
/// <c>InstallAssemblies</c> and read <c>builder.Configuration</c> for the deployment's answers —
/// on both prods that read null (<c>ConfigureMemexMesh</c> had every answer in its parameter and
/// never passed it on), so <c>AiMeshModuleAttribute.ServeFromPartitions(null)</c> put the whole
/// AI catalog on the in-memory path and the AI content sources were never registered: no
/// <c>[StaticRepoImport] Provider</c> on any pod, no <c>provider</c> schema in either database,
/// while the deployed config plainly listed the partitions. The tester/LocalMesh path goes
/// through <c>InstallConfiguredModules</c>, which hands it over — exactly why the defect was
/// portal-specific and invisible to every gate.
///
/// <para>🚨 The assertion is on what the probe module OBSERVED AT FOLD TIME, not merely on
/// <c>builder.Configuration</c> after the call — a hand-over moved to AFTER
/// <c>InstallAssemblies</c> would satisfy the weaker assertion while re-breaking every module,
/// which is precisely the #2507 shape (Copilot review).</para>
/// </summary>
public class ConfigurationHandOverTest
{
    [Fact]
    public void ConfigureMemexMesh_HandsTheHostConfigurationToTheBuilder_BeforeModulesFold()
    {
        var temp = Directory.CreateTempSubdirectory("mw-2507-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Graph:Storage:Type"] = "FileSystem",
                    ["Graph:Storage:BasePath"] = temp,
                    // This test assembly IS the module: its ConfigurationProbeModuleAttribute
                    // records builder.Configuration the moment the fold runs.
                    ["Modules:Assemblies:0"] = typeof(ConfigurationHandOverTest).Assembly.Location,
                })
                .Build();
            var serviceConfigurations = new List<Func<IServiceCollection, IServiceCollection>>();
            var builder = new MeshBuilder(
                configure => serviceConfigurations.Add(configure), new Address("mesh", "test"));

            builder.ConfigureMemexMesh(configuration);

            Assert.Same(configuration, builder.Configuration);

            var services = serviceConfigurations.Aggregate(
                (IServiceCollection)new ServiceCollection(),
                (collection, configure) => configure(collection));
            var observed = services
                .Select(d => d.ImplementationInstance)
                .OfType<ObservedModuleFoldConfiguration>()
                .ToList();
            var observation = Assert.Single(observed);
            Assert.Same(configuration, observation.Value);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }
}

/// <summary>What the probe saw as <c>builder.Configuration</c> when its fold ran — null means the
/// fold ran BEFORE the host handed its configuration over, the #2507 defect.</summary>
public sealed record ObservedModuleFoldConfiguration(IConfiguration? Value);

/// <summary>The probe module attribute — records the fold-time <c>builder.Configuration</c> into
/// the mesh service collection, where the test can read it back.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ConfigurationProbeModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
    [
        builder => builder.ConfigureServices(services =>
            services.AddSingleton(new ObservedModuleFoldConfiguration(builder.Configuration)))
    ];
}
