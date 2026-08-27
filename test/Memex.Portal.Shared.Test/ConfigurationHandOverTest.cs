using Memex.Portal.Shared;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
/// </summary>
public class ConfigurationHandOverTest
{
    [Fact]
    public void ConfigureMemexMesh_HandsTheHostConfigurationToTheBuilder()
    {
        var temp = Directory.CreateTempSubdirectory("mw-2507-").FullName;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Graph:Storage:Type"] = "FileSystem",
                    ["Graph:Storage:BasePath"] = temp,
                })
                .Build();
            var services = new ServiceCollection();
            var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "test"));

            builder.ConfigureMemexMesh(configuration);

            Assert.Same(configuration, builder.Configuration);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }
}
