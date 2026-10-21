using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The <c>defaultApp</c> declaration has to survive DISCOVERY: <c>NodeRepoPackageSource</c> reading
/// <c>content.defaultApp</c> off a package's <c>index.json</c> is the hop that feeds the default-apps
/// lane, and a field present in the authored manifest but dropped here is the silent
/// "authored, never read" defect (the same class as <c>preInstalled</c> and <c>protectedSegments</c>).
/// Only a literal <c>true</c> opts in.
/// </summary>
public class ADefaultAppDeclarationSurvivesDiscoveryTest
{
    private static string Index(string id, string? extra) => $$"""
        {
          "$type": "MeshNode",
          "id": "{{id}}",
          "namespace": "",
          "path": "{{id}}",
          "name": "{{id}}",
          "nodeType": "Store/Plugin",
          "state": "Active",
          "content": {
            "$type": "PluginContent",
            "app": true,
            "preInstalled": true,
            "tier": "free"{{extra}}
          }
        }
        """;

    private static Task<IReadOnlyList<PackageManifest>> Listed() =>
        new NodeRepoPackageSource(
                fetch: (_, _, _, _) => Observable.Return(new RepoSnapshot("deadbeef", new List<RepoFile>
                {
                    new("Threads/index.json", Index("Threads", ",\n    \"defaultApp\": true")),
                    new("Silent/index.json", Index("Silent", null)),
                    new("Stringly/index.json", Index("Stringly", ",\n    \"defaultApp\": \"true\"")),
                    new("Off/index.json", Index("Off", ",\n    \"defaultApp\": false")),
                })),
                repoUrl: "https://github.com/Systemorph/MeshWeaver.Plugins",
                tokenProvider: () => Observable.Return(string.Empty))
            .ListPackages("HEAD").FirstAsync().Timeout(TestTimeouts.Quick).Await(TestContext.Current.CancellationToken);

    [Fact(Timeout = 60_000)]
    public async Task OnlyALiteralTrue_MarksTheManifestAsADefaultApp()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        var listed = await Listed();

        listed.Single(m => m.Id == "Threads").DefaultApp.Should().BeTrue("the authored declaration reaches the manifest");
        listed.Single(m => m.Id == "Threads").PreInstalled.Should().BeTrue("the control on the same read");
        listed.Single(m => m.Id == "Silent").DefaultApp.Should().BeFalse("a root declaring nothing is no default app");
        listed.Single(m => m.Id == "Stringly").DefaultApp.Should().BeFalse("a malformed value never opts a package in");
        listed.Single(m => m.Id == "Off").DefaultApp.Should().BeFalse();
    }
}
