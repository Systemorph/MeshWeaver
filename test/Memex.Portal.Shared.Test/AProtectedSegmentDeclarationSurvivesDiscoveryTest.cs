using System;
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
/// 🚨 <b>The declaration has to survive DISCOVERY, and nothing else in the change set looks at that
/// hop</b> — MeshWeaver#4716, found in review on its pull request.
///
/// <para>Every other test of the protected-segment shape builds a <see cref="PackageManifest"/>
/// in-process, so the projection that actually feeds production — <c>NodeRepoPackageSource</c> reading
/// <c>content.protectedSegments</c> out of a package's <c>index.json</c> — is covered by none of them.
/// That is the EXACT shape of the defect being fixed: the field was present in the authored manifest and
/// core dropped it on the way in, silently, so every installed node-repo package took the blanket
/// <c>PublicRead</c> path. A regression on this hop restores the exposure and reds nothing.</para>
///
/// <para>No network and no git: the source takes its fetch as a delegate, so the whole listing runs off
/// an in-memory <see cref="RepoSnapshot"/> — which also means this test exercises the real
/// <c>ListPackages</c> pipeline rather than a re-implementation of it.</para>
/// </summary>
public class AProtectedSegmentDeclarationSurvivesDiscoveryTest
{
    private const string Declaring = "Feedback";
    private const string Silent = "Chess";

    /// <summary>A plugin root as the plugins repo authors it — the shape live `Feedback/index.json` has.</summary>
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
            "description": "A plugin.",
            "preInstalled": true,
            "tier": "free"{{extra}}
          }
        }
        """;

    private static NodeRepoPackageSource Source() => new(
        fetch: (_, _, _, _) => Observable.Return(new RepoSnapshot("deadbeef", new List<RepoFile>
        {
            // The declaring package: `protectedSegments` beside `preInstalled`, exactly as authored.
            new($"{Declaring}/index.json", Index(Declaring, ",\n    \"protectedSegments\": [\"_Submissions\"]")),
            // The control: an otherwise identical root that declares nothing.
            new($"{Silent}/index.json", Index(Silent, null)),
        })),
        repoUrl: "https://github.com/Systemorph/MeshWeaver.Plugins",
        tokenProvider: () => Observable.Return(string.Empty));

    private static Task<IReadOnlyList<PackageManifest>> Listed() =>
        Source().ListPackages("HEAD")
            .FirstAsync()
            .Timeout(TestTimeouts.Quick)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// THE assertion: the authored declaration reaches the manifest the installer's access step reads.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task TheAuthoredProtectedSegments_ReachTheManifest()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        var listed = await Listed();
        var declaring = listed.Single(m => m.Id == Declaring);

        declaring.PreInstalled.Should().BeTrue(
            "the control on the READ itself: if the projection were broken wholesale, `preInstalled` "
            + "would be false too and the assertion below would be about the wrong thing");
        declaring.ProtectedSegments.Should().Equal(["_Submissions"],
            "THE assertion: `content.protectedSegments` must survive discovery. It did not, and that is "
            + "the whole first half of the defect — the field was authored, core dropped it, and the "
            + "partition took the blanket PublicRead shape with the Store's protection sitting under it "
            + "doing nothing (MeshWeaver#4716)");

        PackageInstaller.DeclaredProtectedPaths(declaring, Declaring).Should().Equal(["Feedback/_Submissions"],
            "and it resolves to the path the access step gates — the two halves joined, so a regression "
            + "in either the read or the resolution is visible here");
    }

    /// <summary>
    /// The other side: a manifest that declares nothing comes back EMPTY, never defaulted to something.
    /// A field that quietly acquires a value would route every package onto the grant-based shape.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AManifestWithoutTheKey_ComesBackEmpty()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        var listed = await Listed();
        listed.Single(m => m.Id == Silent).ProtectedSegments.Should().BeEmpty(
            "absent means absent. A default that carried a segment would gate a child of every package "
            + "that never asked for it, and the shape decision keys on Count > 0");
    }
}
