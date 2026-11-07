using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Apps;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The platform documentation is an APP on every launcher that serves it (Doc/Architecture/AppsHome →
/// "Declared apps"). Before, the app directory listed only package roots with <c>app: true</c> plus
/// Settings, Inbox and Administration, and the Doc root is no package root — so Documentation was
/// missing from every directory launcher, portals and the device mesh alike, while the legacy
/// <c>HomeConfig.DefaultApps</c> that named it fed only the retired records launcher.
/// </summary>
public class DocumentationIsALauncherAppTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private sealed class PartitionedBootstrap : IMeshBootstrap
    {
        public string Name => "monolith-partitioned";

        public MeshBuilder Bootstrap(MeshBuilder builder)
            => builder.UseMonolithMesh().AddPartitionedInMemoryPersistence();
    }

    /// <inheritdoc />
    protected override IMeshBootstrap Bootstrap => new PartitionedBootstrap();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddDocumentation();

    [Fact]
    public void TheDeclaration_OpensTheDocLanding_TopLevel_Localized_BehindReadOnDoc()
    {
        var app = DocumentationExtensions.DocumentationApp;
        app.Id.Should().Be(DocumentationNodeProvider.RootNamespace);
        app.OpenPathFor("alice").Should().Be(DocumentationNodeProvider.RootNamespace);
        app.ProbePathFor("alice").Should().Be(DocumentationNodeProvider.RootNamespace);
        app.LauncherGroup.Should().Be("", "top-level, like the Inbox and Threads");
        app.LabelKey.Should().Be(DocumentationExtensions.DocumentationLabelKey);
        LocalizationCatalog.Get(DocumentationExtensions.DocumentationLabelKey, "de").Should().Be("Doku");
        LocalizationCatalog.Get(DocumentationExtensions.DocumentationLabelKey, "en").Should().Be("Docs");
        app.Name.Should().Be("Docs", "the unlocalized fallback is what a native client paints; \"Documentation\" wrapped mid-word in an 88px tile");
        app.Icon.Should().Be(DocumentationStaticRepoSource.DocIcon);
    }

    [Fact(Timeout = 120_000)]
    public async Task AnyViewer_GetsTheDocumentationTile_FromAddDocumentationAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        Mesh.ServiceProvider.GetServices<DeclaredApp>().Should().Contain(DocumentationExtensions.DocumentationApp);

        var list = await Mesh.ServiceProvider.GetRequiredService<AppDirectory>()
            .Compute("doc-launcher-viewer")
            .Where(l => l.Any(e => e.Id == DocumentationNodeProvider.RootNamespace))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var docs = list.Single(e => e.Id == DocumentationNodeProvider.RootNamespace);
        docs.OpenPath.Should().Be(DocumentationNodeProvider.RootNamespace);
        docs.BuiltIn.Should().BeTrue();
        list.Select(e => e.Id).Take(2).Should().Equal(AppDirectory.SettingsId, AppDirectory.InboxId);
    }
}
