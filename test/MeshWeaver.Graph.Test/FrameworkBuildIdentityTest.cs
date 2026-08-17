#pragma warning disable CS1591

using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Plugin.Build;
using Xunit;

using MeshWeaver.Compiler;
namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ONE framework build identity (#1660 WS3, anchored on MeshWeaver.Compiler since
/// #1707): the API-surface scheme ("rebuild only when we need to" — content rebakes exactly when
/// the surface it compiles against changes), its fallback chain (commit stamp → toolchain-anchor
/// MVID), and — the pin the whole CI bake stands on — that every reader resolves the SAME value
/// for the same inputs. The process-level tests run in both build flavors on purpose: this test
/// host ships no surface manifest, so it exercises the fallback chain exactly as a manifest-less
/// CI test process does.
/// </summary>
public class FrameworkBuildIdentityTest
{
    /// <summary>The identity anchor — the toolchain assembly (#1707).</summary>
    private static readonly System.Reflection.Assembly AnchorAssembly =
        typeof(FrameworkBuildIdentity).Assembly;

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
        // MeshWeaver.Compiler IS the NodeType compile toolchain (#1707): a BODY-ONLY change
        // there alters the GENERATED input of every compile without any API change, so its full
        // implementation MVID — not its reference-assembly hash — joins the identity.
        string? MvidV1(string name) => name == "MeshWeaver.Compiler" ? "impl-mvid-1" : null;
        string? MvidV2(string name) => name == "MeshWeaver.Compiler" ? "impl-mvid-2" : null;

        var v1 = FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, MvidV1);
        var v2 = FrameworkBuildIdentity.ComputeSurfaceIdentity(BaselinePairs, MvidV2);
        v1.Should().NotBe(v2,
            "an emitter change must rebake even though the surface pairs are identical");

        // …and with the impl MVID pinned, the toolchain's REF-ASM hash no longer participates:
        // its surface entry may change without moving the identity (the impl MVID already
        // covers it).
        var compilerSurfaceChanged = new Dictionary<string, string>(BaselinePairs, StringComparer.Ordinal)
        {
            ["MeshWeaver.Compiler"] = "different-surface",
        };
        FrameworkBuildIdentity.ComputeSurfaceIdentity(compilerSurfaceChanged, MvidV1)
            .Should().Be(v1);
    }

    [Fact]
    public void FullMvidMembership_IsTheToolchainClosureIncludingItsDirectDependencies()
    {
        // The membership IS the design (#1707, maintainer 2026-08-17: "must track dependencies of
        // the compiler itself — if any have changed, need to recompile"): the toolchain roots
        // (MeshWeaver.Compiler — everything that shapes generated compile input — and
        // MeshWeaver.NuGet, the #r directive parser/resolver) PLUS their MeshWeaver dependency
        // closure, because the toolchain CALLS into what it links, so a body-only change in a
        // closure member can change what it emits with no API change.
        var members = FrameworkBuildIdentity.FullMvidAssemblies;

        members.Should().Contain("MeshWeaver.Compiler").And.Contain("MeshWeaver.NuGet",
            "the roots are always members");
        members.Should().OnlyContain(n => n.StartsWith("MeshWeaver.", StringComparison.Ordinal),
            "non-MeshWeaver dependencies roll with the image/TFM and stay outside the identity");
        // Known DIRECT dependencies of the toolchain — a regression here means the closure walk
        // silently stopped resolving references.
        members.Should().Contain("MeshWeaver.Mesh.Contract").And.Contain("MeshWeaver.ContentCollections");
        members.Should().Equal(members.OrderBy(n => n, StringComparer.Ordinal),
            "the closure is sorted so the hash text is deterministic");
    }

    [Fact]
    public void ToolchainClosure_WalksTransitivesAndFiltersNonMeshWeaver()
    {
        // The pure closure rule over a staged graph: transitive MeshWeaver refs join, diamonds
        // dedupe, non-MeshWeaver refs are dropped, and a name with no resolvable refs still
        // joins (its MVID then resolves 'absent', which is itself identity-relevant).
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["MeshWeaver.Compiler"] = ["MeshWeaver.A", "System.Runtime", "MeshWeaver.B"],
            ["MeshWeaver.NuGet"] = ["MeshWeaver.B", "NuGet.Protocol"],
            ["MeshWeaver.A"] = ["MeshWeaver.C"],
            ["MeshWeaver.B"] = [],
        };
        var closure = FrameworkBuildIdentity.ComputeToolchainClosure(
            ["MeshWeaver.Compiler", "MeshWeaver.NuGet"],
            name => graph.TryGetValue(name, out var refs) ? refs : []);
        closure.Should().Equal(
            "MeshWeaver.A", "MeshWeaver.B", "MeshWeaver.C",
            "MeshWeaver.Compiler", "MeshWeaver.NuGet");
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
            File.WriteAllText(
                Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName),
                string.Join('\n', FrameworkBuildIdentity.ContentSurfaceAssemblies.Select(n => $"{n}=stub")));

            var identity = FrameworkBuildIdentity.ResolveProcessIdentity(dir, AnchorAssembly);
            identity.Should().StartWith("s");
            // The full-MVID exceptions (the toolchain closure) resolve their LIVE MVIDs in this
            // process — mirrored here with the same loaded-else-PE resolution the identity uses —
            // while everything else resolves its manifest stub.
            var expected = FrameworkBuildIdentity.ComputeSurfaceIdentity(
                FrameworkBuildIdentity.ContentSurfaceAssemblies.ToDictionary(n => n, _ => "stub"),
                TestImplMvidOf);
            identity.Should().Be(expected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProcessIdentity_DegradesToTheFallback_WhenTheManifestIsUnreadable()
    {
        // A torn/unreadable manifest must cost a conservative fallback identity + a warning —
        // NEVER a throw: the resolution runs on the boot path (Copilot finding, PR #1696).
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("stages unreadability via unix file modes — POSIX hosts only (CI is linux)");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "mw-torn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var manifest = Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName);
        File.WriteAllText(manifest, "MeshWeaver.Data=abc");
        try
        {
            File.SetUnixFileMode(manifest, UnixFileMode.None);
            // Root (or a non-POSIX host) can still read it — then this scenario cannot be staged.
            var unreadable = false;
            try { File.ReadAllText(manifest); } catch { unreadable = true; }
            Assert.SkipWhen(!unreadable, "cannot stage an unreadable file on this host");

            var (identity, warning) =
                FrameworkBuildIdentity.ResolveProcessIdentityWithDiagnostics(dir, AnchorAssembly);
            identity.Should().Be(FrameworkBuildIdentity.Resolve(
                    FrameworkBuildIdentity.StampedIdentityOf(AnchorAssembly),
                    AnchorAssembly.ManifestModule.ModuleVersionId.ToString("N")),
                "an unreadable manifest degrades to the stamp/MVID layer");
            warning.Should().NotBeNullOrEmpty("the degradation must be sayable where the identity is announced");
        }
        finally
        {
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProcessIdentity_DegradesToTheFallback_WhenTheManifestHoldsNothingUsable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName),
                "not-a-pair\n\n");
            var (identity, warning) =
                FrameworkBuildIdentity.ResolveProcessIdentityWithDiagnostics(dir, AnchorAssembly);
            identity.Should().NotStartWith("s");
            warning.Should().NotBeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProcessIdentity_FallsBackToStampThenMvid_WithoutAManifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-nomanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FrameworkBuildIdentity.ResolveProcessIdentity(dir, AnchorAssembly)
                .Should().Be(FrameworkBuildIdentity.Resolve(
                    FrameworkBuildIdentity.StampedIdentityOf(AnchorAssembly),
                    AnchorAssembly.ManifestModule.ModuleVersionId.ToString("N")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FrameworkVersion_IsTheProcessIdentityOfTheCompilerAssembly()
    {
        var expected = FrameworkBuildIdentity.ResolveProcessIdentity(
            AppContext.BaseDirectory, AnchorAssembly);

        FrameworkBuildIdentity.FrameworkVersion.Should().Be(expected,
            "there is exactly one identity resolution; every consumer flows from it");
        NodeTypeCompilationHelpers.FrameworkVersion.Should().Be(expected,
            "the Graph-side shim must delegate, never re-resolve");
        PrebuiltAssemblySeeder.LiveFrameworkMvid.Should().Be(expected,
            "the producer-facing public reading must never diverge from the gate");
    }

    [Fact]
    public void PeRead_AgreesWithTheLoadedAssembly_OnTheFallbackLayer()
    {
        // The PE-level reading (a producer inspecting a restored MeshWeaver.Compiler.dll — the
        // identity anchor, Plugin.Build's IdentityAssembly) covers the FALLBACK layer —
        // stamp-or-MVID. In this manifest-less test host that IS the live identity, so the two
        // must agree; in a manifest-shipping host the surface identity supersedes both and the
        // PE reading serves as provenance only.
        FrameworkIdentity.IdentityAssembly.Should().Be(AnchorAssembly.GetName().Name,
            "the packer reads the identity off the SAME assembly the runtime anchors on");
        AnchorAssembly.Location.Should().NotBeNullOrEmpty();

        FrameworkIdentity.ReadIdentity(AnchorAssembly.Location)
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

    /// <summary>The identity's ImplMvidOf resolution, mirrored independently: loaded assembly,
    /// else a metadata-only read of the DLL beside the test host, else null.</summary>
    private static string? TestImplMvidOf(string simpleName)
    {
        if (LoadedMvidOf(simpleName) is { } loaded)
            return loaded;
        var candidate = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return null;
        using var stream = File.OpenRead(candidate);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
    }

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
