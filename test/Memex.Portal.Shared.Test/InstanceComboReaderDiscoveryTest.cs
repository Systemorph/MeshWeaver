#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The combo reader's discovery leg reads <c>Admin/_Discovery/*</c> — the ONE place
/// <see cref="ModuleDiscovery.PathFor"/> writes discovery records — with an ANCHORED query.
///
/// <para>Until 2026-10-04 it read them with a declared mesh-wide fan-out. On PostgreSQL a declared
/// fan-out unions <c>public.searchable_schemas</c>, which never includes the Admin partition, so the
/// leg could not find a single record and still unioned every partition schema once an hour
/// (memex, 12 h: 6 slow runs over 251 schemas). The in-memory store this fixture runs on DOES see
/// Admin from a fan-out, so this test pins the behaviour (a written record is read and the
/// "no discovery record" caveat goes away) and the census below pins the shape.</para>
/// </summary>
public class InstanceComboReaderDiscoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    [Fact(Timeout = 120_000)]
    public async Task ReadsTheDiscoveryRecordUnderAdmin()
    {
        var reader = new InstanceComboReader(Mesh);

        var before = await reader.Read()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        before.Caveats.Should().Contain(InstanceComboReader.NoDiscoveryCaveat,
            "the negative control: with no record under Admin/_Discovery the reader says so");

        var path = ModuleDiscovery.PathFor("https://github.com/acme/widgets");
        path.Should().StartWith("Admin/_Discovery/");
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath(path) with
                {
                    NodeType = ModuleDiscovery.NodeType,
                    Name = "acme/widgets",
                    State = MeshNodeState.Active,
                    Content = new ModuleDiscovery
                    {
                        RepositoryUrl = "https://github.com/acme/widgets",
                        SourceName = "Widgets",
                        GitRef = "main",
                        LastScannedAt = DateTimeOffset.UtcNow,
                        Modules = ImmutableList<DiscoveredModule>.Empty,
                    },
                }))
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var after = await reader.Read()
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        after.Caveats.Should().NotContain(InstanceComboReader.NoDiscoveryCaveat,
            "the record at Admin/_Discovery/acme.widgets is exactly what the discovery leg reads");
    }
}
