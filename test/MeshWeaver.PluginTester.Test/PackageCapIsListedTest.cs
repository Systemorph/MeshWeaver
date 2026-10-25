#pragma warning disable CS1591

using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The authored upper platform cap (<c>content.maxMeshVersion</c>, policy
/// <c>package-mesh-version-cap</c>) and its two companions are CARRIED by the catalog listing onto
/// <see cref="PackageManifest"/>, next to the floor, so nothing that reads the manifest sees a capped
/// package as uncapped. The negative controls: an uncapped package and a blank cap both read as none.
/// </summary>
public class PackageCapIsListedTest
{
    private static string Root(string id, string content) =>
        "{\"$type\":\"MeshNode\",\"id\":\"" + id + "\",\"namespace\":\"\",\"path\":\"" + id + "\",\"mainNode\":\""
        + id + "\",\"name\":\"" + id + "\",\"nodeType\":\"Store/Plugin\",\"state\":\"Active\",\"content\":" + content + "}";

    [Fact(Timeout = 60_000)]
    public async Task TheCapAndItsCompanions_RideTheManifest_AndAnAbsentCapIsNone()
    {
        var snapshot = new RepoSnapshot("0123456789abcdef0123456789abcdef01234567",
        [
            new RepoFile("Capped/index.json", Root("Capped",
                "{\"$type\":\"PluginContent\",\"minMeshVersion\":\"3.1.10300\",\"maxMeshVersion\":\" 3.1.10400 \","
                + "\"capReason\":\"needs the old IFoo shape\",\"capTracking\":\"acme/Feedback/migrate-capped\"}")),
            new RepoFile("Free/index.json", Root("Free",
                "{\"$type\":\"PluginContent\",\"minMeshVersion\":\"3.1.10300\"}")),
            new RepoFile("Blank/index.json", Root("Blank",
                "{\"$type\":\"PluginContent\",\"maxMeshVersion\":\"   \",\"capReason\":\"\"}")),
        ]);
        var source = new NodeRepoPackageSource((_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");

        var listed = (await source.ListPackages("HEAD").FirstAsync().Await(TestContext.Current.CancellationToken))
            .ToDictionary(p => p.Id);

        Assert.Equal(new[] { "Blank", "Capped", "Free" }, listed.Keys.OrderBy(k => k).ToArray());
        var capped = listed["Capped"];
        Assert.Equal("3.1.10300", capped.MinMeshVersion);
        Assert.Equal("3.1.10400", capped.MaxMeshVersion);
        Assert.Equal("needs the old IFoo shape", capped.CapReason);
        Assert.Equal("acme/Feedback/migrate-capped", capped.CapTracking);

        // NEGATIVE controls: no cap authored, or a blank one, is no cap at all.
        Assert.Null(listed["Free"].MaxMeshVersion);
        Assert.Null(listed["Free"].CapReason);
        Assert.Null(listed["Free"].CapTracking);
        Assert.Equal("3.1.10300", listed["Free"].MinMeshVersion);
        Assert.Null(listed["Blank"].MaxMeshVersion);
        Assert.Null(listed["Blank"].CapReason);
    }
}
