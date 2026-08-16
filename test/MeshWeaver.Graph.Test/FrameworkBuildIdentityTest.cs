#pragma warning disable CS1591

using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Build;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ONE framework build identity (#1660 WS3): the API-surface scheme ("rebuild only when
/// we need to" — content rebakes exactly when the surface it compiles against changes), its
/// fallback chain (commit stamp → Graph MVID), and — the pin the whole CI bake stands on — that
/// every reader resolves the SAME value for the same inputs. The process-level tests run in both
/// build flavors on purpose: this test host ships no surface manifest, so it exercises the
/// fallback chain exactly as a manifest-less CI test process does.
/// </summary>
public class FrameworkBuildIdentityTest
{
    private static readonly IReadOnlyDictionary<string, string> BaselinePairs =
        FrameworkBuildIdentity.ContentSurfaceAssemblies
            .ToDictionary(n => n, n => "hash-of-" + n, StringComparer.Ordinal);

    private static string? NoMvid(string _) => null;

    // ---- the surface computation (pure) --------------------------------------------------------

    [Fact]
    public void SurfaceIdentity_IsStableForIdenticalInputs()
    {
        var a = FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, NoMvid);
        var b = FrameworkBuildIdentity.ComputeSurfaceIdentity(
            new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal), NoMvid);
        a.Should().Be(b);
        a.Should().MatchRegex("^s[0-9a-f]{32}$",
            "the identity shape is 's' + hex — the store tag takes its first 8 chars");
    }

    [Fact]
    public void SurfaceIdentity_ChangesWhenAListedAssemblysSurfaceChanges()
    {
        var changed = new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal)
        {
            ["MeshWeaver.Layout"] = "a-public-member-was-added",
        };
        FrameworkBuildIdentity.ComputeSurfaceIdentity(changed, NoMvid)
            .Should().NotBe(FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, NoMvid),
                "a surface change in a content-facing assembly must rebake");
    }

    [Fact]
    public void SurfaceIdentity_IgnoresAssembliesOutsideTheCanonicalList()
    {
        // The portal's closure carries Blazor/Orleans/host assemblies the bake host never loads —
        // they are OUTSIDE the content surface, so their presence (or change) must not fork the
        // identity between the two hosts.
        var withHostExtras = new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal)
        {
            ["MeshWeaver.Blazor"] = "portal-only",
            ["MeshWeaver.Hosting.Orleans"] = "portal-only",
        };
        FrameworkBuildIdentity.ComputeSurfaceIdentity(withHostExtras, NoMvid)
            .Should().Be(FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, NoMvid));
    }

    [Fact]
    public void SurfaceIdentity_UsesTheFullImplMvidForGeneratorAssemblies()
    {
        // MeshWeaver.Graph carries the NodeType compile pipeline's emitters: a BODY-ONLY change
        // there alters the GENERATED input of every compile without any API change, so its full
        // implementation MVID — not its reference-assembly hash — joins the identity.
        string? MvidV1(string name) => name == "MeshWeaver.Graph" ? "impl-mvid-1" : null;
        string? MvidV2(string name) => name == "MeshWeaver.Graph" ? "impl-mvid-2" : null;

        var v1 = FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, MvidV1);
        var v2 = FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, MvidV2);
        v1.Should().NotBe(v2,
            "an emitter change must rebake even though the surface pairs are identical");

        // …and with the impl MVID pinned, Graph's REF-ASM hash no longer participates: its
        // surface entry may change without moving the identity (the impl MVID already covers it).
        var graphSurfaceChanged = new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal)
        {
            ["MeshWeaver.Graph"] = "different-surface",
        };
        FrameworkBuildIdentity.ComputeSurfaceIdentity(graphSurfaceChanged, MvidV1)
            .Should().Be(v1);
    }

    [Fact]
    public void SurfaceIdentity_RecordsAbsence()
    {
        // A host missing a canonical assembly is a DIFFERENT surface reality — it must never
        // share an identity with a host that has it.
        var missing = new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal);
        missing.Remove("MeshWeaver.Maps");
        FrameworkBuildIdentity.ComputeSurfaceIdentity(missing, NoMvid)
            .Should().NotBe(FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, NoMvid));
    }

    [Fact]
    public void ManifestParsing_RoundTrips()
    {
        var parsed = FrameworkBuildIdentity.ParseSurfaceManifest(
            "MeshWeaver.Data=ABC123\n\nnot-a-pair\nMeshWeaver.Layout=DEF456\n=orphan\ntrailing=\n");
        parsed.Should().HaveCount(2);
        parsed["MeshWeaver.Data"].Should().Be("ABC123");
        parsed["MeshWeaver.Layout"].Should().Be("DEF456");
    }

    // ---- the canonical list --------------------------------------------------------------------

    [Fact]
    public void CanonicalList_IsSortedAndDistinct_AndContainsTheExceptions()
    {
        var list = FrameworkBuildIdentity.ContentSurfaceAssemblies.ToList();
        list.Should().Equal(list.OrderBy(n => n, StringComparer.Ordinal),
            "the hash text is built in list order — an unsorted list would make it depend on "
            + "edit history");
        list.Should().OnlyHaveUniqueItems();
        foreach (var exception in FrameworkBuildIdentity.FullMvidAssemblies)
            list.Should().Contain(exception,
                "a full-MVID exception outside the canonical set would never be hashed at all");
    }

    [Fact]
    public void CanonicalList_MatchesTheTesterClosure()
    {
        // The list IS the bake host's framework closure minus its two host assemblies — the
        // surface shipped content can compile against, enforced by the gate itself. This test
        // recomputes that closure from the csproj graph so the list cannot silently drift when
        // the tester gains or loses a framework reference.
        var root = FindRepositoryRoot();
        Assert.SkipWhen(root is null,
            "repository tree not reachable from the test bin — closure pin runs in-repo only");

        var closure = ProjectClosure(Path.Combine(
            root!, "tools", "MeshWeaver.PluginTester", "MeshWeaver.PluginTester.csproj"));
        var expected = closure
            .Where(n => n.StartsWith("MeshWeaver.", StringComparison.Ordinal))
            .Where(n => n is not "MeshWeaver.PluginTester" and not "MeshWeaver.Hosting.Monolith")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        FrameworkBuildIdentity.ContentSurfaceAssemblies.ToList().Should().Equal(expected,
            "the canonical content-surface list must follow the bake host's closure — update "
            + "FrameworkBuildIdentity.ContentSurfaceAssemblies to match");
    }

    // ---- the process resolution chain ----------------------------------------------------------

    [Fact]
    public void ProcessIdentity_UsesTheManifestWhenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-surface-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var graph = typeof(NodeTypeCompilationHelpers).Assembly;
            File.WriteAllText(
                Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName),
                string.Join('\n', FrameworkBuildIdentity.ContentSurfaceAssemblies.Select(n => $"{n}=stub")));

            var identity = FrameworkBuildIdentity.ResolveProcessIdentity(dir, graph);
            identity.Should().StartWith("s");
            // Graph is a full-MVID exception and IS loaded, so its live MVID joins the hash — the
            // rest resolve their manifest stubs.
            var expected = FrameworkBuildIdentity.ComputeSurfaceIdentity(
                FrameworkBuildIdentity.ContentSurfaceAssemblies.ToDictionary(n => n, _ => "stub"),
                n => n == "MeshWeaver.Graph"
                    ? graph.ManifestModule.ModuleVersionId.ToString("N")
                    : LoadedMvidOf(n));
            identity.Should().Be(expected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProcessIdentity_FallsBackToStampThenMvid_WithoutAManifest()
    {
        var graph = typeof(NodeTypeCompilationHelpers).Assembly;
        var dir = Path.Combine(Path.GetTempPath(), "mw-nomanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FrameworkBuildIdentity.ResolveProcessIdentity(dir, graph)
                .Should().Be(FrameworkBuildIdentity.Resolve(
                    FrameworkBuildIdentity.StampedIdentityOf(graph),
                    graph.ManifestModule.ModuleVersionId.ToString("N")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FrameworkVersion_IsTheProcessIdentityOfTheGraphAssembly()
    {
        var graph = typeof(NodeTypeCompilationHelpers).Assembly;
        var expected = FrameworkBuildIdentity.ResolveProcessIdentity(AppContext.BaseDirectory, graph);

        NodeTypeCompilationHelpers.FrameworkVersion.Should().Be(expected,
            "there is exactly one identity resolution; every consumer flows from it");
        PrebuiltAssemblySeeder.LiveFrameworkMvid.Should().Be(expected,
            "the producer-facing public reading must never diverge from the gate");
    }

    [Fact]
    public void PeRead_AgreesWithTheLoadedAssembly_OnTheFallbackLayer()
    {
        // The PE-level reading (a producer inspecting a restored Graph.dll) covers the FALLBACK
        // layer — stamp-or-MVID. In this manifest-less test host that IS the live identity, so
        // the two must agree; in a manifest-shipping host the surface identity supersedes both
        // and the PE reading serves as provenance only.
        var graph = typeof(NodeTypeCompilationHelpers).Assembly;
        graph.Location.Should().NotBeNullOrEmpty();

        FrameworkIdentity.ReadIdentity(graph.Location)
            .Should().Be(PrebuiltAssemblySeeder.LiveFrameworkMvid);
    }

    [Fact]
    public void StoreTag_IsAlwaysAttributableByTheRetentionSweep()
    {
        // FileSystemAssemblyStore's filename tag is FrameworkVersion[..8]; the retention sweep
        // only ever deletes files whose tag it can attribute (AssemblyCacheGenerations.TagOf).
        // Whatever flavor built this test run, the live tag must round-trip — otherwise every
        // generation this build writes would be unreclaimable. The surface shape ('s' + 7 hex)
        // is pinned explicitly because no local test run produces it live.
        var tag = NodeTypeCompilationHelpers.FrameworkVersion[..8];
        AssemblyCacheGenerations.TagOf($"v7-{tag}-9f4455cd1122.dll")
            .Should().Be(tag.ToLowerInvariant());
        AssemblyCacheGenerations.TagOf("v7-s22825f5-9f4455cd1122.dll")
            .Should().Be("s22825f5");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string? LoadedMvidOf(string simpleName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => !a.IsDynamic
                && string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal))
            ?.ManifestModule.ModuleVersionId.ToString("N");

    private static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static HashSet<string> ProjectClosure(string startProject)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(startProject));
        while (stack.Count > 0)
        {
            var project = stack.Pop();
            if (!seen.Add(project) || !File.Exists(project))
                continue;
            names.Add(Path.GetFileNameWithoutExtension(project));
            var directory = Path.GetDirectoryName(project)!;
            foreach (Match m in Regex.Matches(
                File.ReadAllText(project), "ProjectReference Include=\"([^\"]+)\""))
                stack.Push(Path.GetFullPath(
                    Path.Combine(directory, m.Groups[1].Value.Replace('\\', '/'))));
        }
        return names;
    }
}
