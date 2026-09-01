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

    /// <summary>
    /// 🚨 THE REBAKE BOUNDARY IS PINNED, because widening it is silent and expensive.
    ///
    /// <para>Every member here contributes its FULL implementation MVID to the framework identity,
    /// so a body-only commit to ANY of them mints a new identity, empties the assembly share's
    /// key-space and rebakes every NodeType on every deployment. The set is COMPUTED (the closure
    /// walk above), which is what keeps a new toolchain dependency from being silently OUTSIDE the
    /// identity — and is also why one added <c>ProjectReference</c> anywhere in the closure can pull
    /// a whole subtree INSIDE it without a line of this file changing.</para>
    ///
    /// <para>That already happened. #1712 moved the boundary off <c>MeshWeaver.Graph</c> (311
    /// commits/30d, the single highest-churn assembly) and the walk pulled in <c>Mesh.Contract</c>
    /// (190), <c>Messaging.Hub</c> (135), <c>Data</c> (59) and <c>Layout</c> (40) behind it — 383
    /// commits/30d for the union, so the identity now moves MORE often than before, while #1707's
    /// stated acceptance ("a body-only edit in MeshWeaver.Graph rebakes nothing") reads as passed.
    /// See #1976.</para>
    ///
    /// <para><b>When this fails, do not just update the list.</b> Ask which reference widened the
    /// closure and whether the toolchain actually needs it; a member added here is a member every
    /// deployment now rebakes on.</para>
    /// </summary>
    [Fact]
    public void FullMvidClosure_IsExactly_TheKnownSet()
    {
        string[] expected =
        [
            "MeshWeaver.Compiler",              // root — shapes generated compile input
            "MeshWeaver.NuGet",                 // root — the #r directive parser/resolver
            "MeshWeaver.ContentCollections",    // ↓ pulled in transitively from the roots
            "MeshWeaver.Data",
            "MeshWeaver.Data.Contract",
            "MeshWeaver.Domain",
            "MeshWeaver.Kernel",
            "MeshWeaver.Layout",
            "MeshWeaver.Markdown",
            "MeshWeaver.Mesh.Contract",
            "MeshWeaver.Messaging.Contract",
            "MeshWeaver.Messaging.Hub",
            "MeshWeaver.Reflection",
            "MeshWeaver.ServiceProvider",
            "MeshWeaver.ShortGuid",
            "MeshWeaver.Utils",
        ];

        FrameworkBuildIdentity.FullMvidAssemblies.Should().Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            "the full-MVID closure is the set whose every commit rebakes the world — a change here "
            + "means a ProjectReference widened (or narrowed) the toolchain's dependency graph. "
            + "Widening: find the reference and ask whether the toolchain needs it (#1976). "
            + "Narrowing: that is the goal — delete the line and say so in the PR.");
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
        // 🚨 Assert the REMOVAL, not just the identities. This test named MeshWeaver.Maps until Maps
        // left the content surface, at which point Remove() would have removed nothing and the two
        // identities would have been compared as equals — a guard checking nothing. The name has to
        // be one the baseline actually carries, and the only way to keep that true is to say so.
        missing.Remove("MeshWeaver.Layout").Should().BeTrue(
            "the assembly this test removes must be IN the baseline, or it proves nothing about absence");
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


    // ---- the identity must be the SAME on every host that resolves it (#1814) -------------------

    [Fact]
    public void CanonicalContentSurface_IsRecordedByEverySurfaceManifestHost()
    {
        // 🚨 THE PIN FOR #1814. The identity is an ADDRESS: the bake publishes bundles under the
        // identity ITS host resolves and a portal only ever looks under the identity IT resolves. A
        // canonical assembly that is absent from one host's manifest hashes as AbsentMarker THERE and
        // as a real surface id on the other, so the two hosts of ONE commit resolve two identities and
        // every bake lands at an address nobody reads — silently: publication succeeds, the job is
        // green, and the pods simply compile everything at boot.
        //
        // That is exactly what happened. `feat: Excel/CSV import becomes its own module` (82481e024,
        // merged 2026-08-17 18:46) moved MeshWeaver.Import and its private closure — MeshWeaver.
        // DataSetReader{,.Csv,.Excel,.Excel.BinaryFormat,.Excel.OpenXmlFormat,.Excel.Utils} and
        // MeshWeaver.DataStructures, EIGHT canonical names — out of both portals' COMPILE reference
        // graphs into the modules/<Name>/ runtime lane. The surface manifest is written from
        // @(ReferencePathWithRefAssemblies), so those eight lines vanished from the image's manifest
        // while mw-plugin-test kept them. Measured on the shipped images of release 3.0.0-rc4.ci.4276
        // (both linux/amd64): mw-plugin-test resolved s7293e54297ec28e213bd82f30d59e709 and
        // memex-portal-ai resolved sa6d587a25d64d11774f22348664bca0c — the value the live pods logged.
        // The 29 SHARED manifest entries had byte-identical hashes; presence, not drift, was the whole
        // difference. Two hours of every course cover serving a compilation-fallback card followed.
        //
        // CanonicalList_MatchesTheTesterClosure pins one side of that equality. This pins the other,
        // and it is the check whose absence let a one-line-per-host change take the site down.
        var root = FindRepositoryRoot();
        Assert.SkipWhen(root is null,
            "repository tree not reachable from the test bin — closure pin runs in-repo only");

        var hosts = SurfaceManifestHosts(root!);
        // 🚨 The agreement invariant became CROSS-REPO when the GUI left. The portal hosts
        // (Memex.Portal.Monolith, Memex.Portal.Distributed) moved to MeshWeaver.Plugins, so the two
        // hosts that disagreed in #1814 — mw-plugin-test and memex-portal-ai — no longer live in one
        // tree and NO single-repo test can compare them. This is one half of the old check: every
        // surface-manifest host THIS repo still ships must carry the complete canonical surface. The
        // other half is the plugins gate running the identical closure check against its portal host.
        // Both halves are required; neither alone reproduces what the original assertion did.
        //
        // Pinned BY NAME rather than by count. Relaxing >1 to >0 would let a broken discovery that
        // happened to match some unrelated csproj read as a pass — exactly the failure mode the
        // original count bar existed to refuse.
        hosts.Should().NotBeEmpty(
            "a discovery that found no surface-manifest host has verified nothing, so it must not "
            + "read as a pass");
        hosts.Select(Path.GetFileNameWithoutExtension)
            .Should().Contain("MeshWeaver.PluginTester",
                "the tester is this repo's remaining surface-manifest host after the GUI left — if "
                + "discovery stops finding it, the closure check below is running on the wrong set");

        var missingByHost = hosts.ToDictionary(
            host => Path.GetRelativePath(root!, host),
            host =>
            {
                var closure = ProjectClosure(host);
                return FrameworkBuildIdentity.ContentSurfaceAssemblies
                    .Where(name => !closure.Contains(name))
                    .ToList();
            },
            StringComparer.Ordinal);

        missingByHost.Values.SelectMany(m => m).Should().BeEmpty(
            "every host that ships a surface manifest resolves the framework identity, so each must "
            + "record EVERY canonical content-surface assembly — a missing one hashes as 'absent' "
            + "there and forks that host's identity away from the bake's. Missing per host: "
            + string.Join(" | ", missingByHost
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"))
            + ". Fix by giving the host the compile reference back (Private=\"false\" "
            + "ExcludeAssets=\"runtime\" keeps the bits out of its app closure — that is how "
            + "Memex.Portal.Distributed references MeshWeaver.Import) — never by shrinking "
            + "ContentSurfaceAssemblies, which would under-invalidate and let a portal adopt "
            + "NodeType assemblies compiled against a framework that has shifted underneath them.");
    }

    // ---- resolving ANOTHER host's identity from its binaries (the CI address check) --------------

    [Fact]
    public void ResolveIdentityForDirectory_MatchesTheProcessComputationForTheSameInputs()
    {
        // The verb CI runs (`mw-plugin-test framework-identity <app-dir>`) must answer for a FOREIGN
        // /app exactly what that host answers for itself — otherwise the guard compares its own
        // arithmetic, not the two hosts.
        var dir = StageAppDirectory(FrameworkBuildIdentity.ContentSurfaceAssemblies);
        try
        {
            var (identity, problem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(dir);
            problem.Should().BeNull();
            identity.Should().NotBeNull().And.MatchRegex("^s[0-9a-f]{32}$");

            // Recomputed independently from the same directory: manifest pairs for everything, the
            // toolchain closure's members by implementation MVID read off the staged DLLs.
            var pairs = FrameworkBuildIdentity.ParseSurfaceManifest(
                File.ReadAllText(Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName)));
            var closure = FrameworkBuildIdentity.ComputeToolchainClosure(
                FrameworkBuildIdentity.ToolchainRoots, name => ReferencedInDirectory(dir, name));
            identity.Should().Be(FrameworkBuildIdentity.ComputeSurfaceIdentity(
                pairs, name => MvidInDirectory(dir, name), closure));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveIdentityForDirectory_ForksWhenOneCanonicalAssemblyIsUnrecorded()
    {
        // #1814 in miniature: same binaries, one manifest missing a canonical name. The two hosts
        // MUST resolve different identities (that is the mechanism working correctly) and the
        // absence must be REPORTABLE by name — "the hashes differ" is not an actionable failure.
        // Any ONE canonical assembly; the point is that dropping a single member forks the
        // identity and is reportable BY NAME. (It named MeshWeaver.Import until that module left
        // the platform — one constant now, so the two assertions cannot drift apart.)
        const string dropped = "MeshWeaver.Graph";
        var complete = StageAppDirectory(FrameworkBuildIdentity.ContentSurfaceAssemblies);
        var reduced = StageAppDirectory(
            FrameworkBuildIdentity.ContentSurfaceAssemblies.Where(n => n != dropped));
        try
        {
            var (whole, _) = FrameworkBuildIdentity.ResolveIdentityForDirectory(complete);
            var (partial, _) = FrameworkBuildIdentity.ResolveIdentityForDirectory(reduced);
            whole.Should().NotBeNull();
            partial.Should().NotBeNull().And.NotBe(whole,
                "a host that does not record a canonical assembly is a different surface reality — "
                + "it must never share the bake's address");

            var pairs = FrameworkBuildIdentity.ParseSurfaceManifest(
                File.ReadAllText(Path.Combine(reduced, FrameworkBuildIdentity.SurfaceManifestFileName)));
            FrameworkBuildIdentity.CanonicalAssembliesAbsentFrom(pairs)
                .Should().Equal(dropped);
            FrameworkBuildIdentity.CanonicalAssembliesAbsentFrom(
                    FrameworkBuildIdentity.ParseSurfaceManifest(
                        File.ReadAllText(Path.Combine(complete, FrameworkBuildIdentity.SurfaceManifestFileName))))
                .Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(complete, recursive: true);
            Directory.Delete(reduced, recursive: true);
        }
    }

    [Fact]
    public void ResolveIdentityForDirectory_RefusesToAnswerWithoutAUsableManifest()
    {
        // 🚨 A guard that cannot fail is not a guard. If a manifest-less directory degraded to the
        // stamp/MVID fallback, two manifest-less hosts of one commit would resolve the SAME value and
        // the CI comparison would report a match having proven nothing. So: no identity, and a
        // diagnostic that says which file is missing.
        var dir = Path.Combine(Path.GetTempPath(), "mw-nomanifest-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (identity, problem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(dir);
            identity.Should().BeNull();
            problem.Should().Contain(FrameworkBuildIdentity.SurfaceManifestFileName);

            File.WriteAllText(
                Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName), "not-a-pair\n\n");
            var (stillNone, unusable) = FrameworkBuildIdentity.ResolveIdentityForDirectory(dir);
            stillNone.Should().BeNull();
            unusable.Should().NotBeNullOrEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveIdentityForDirectory_ReportsAMissingDirectory()
    {
        var (identity, problem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(
            Path.Combine(Path.GetTempPath(), "mw-absent-" + Guid.NewGuid().ToString("N")));
        identity.Should().BeNull();
        problem.Should().NotBeNullOrEmpty();
    }

    /// <summary>Stages an /app-shaped directory: this test host's MeshWeaver assemblies (so the
    /// toolchain closure and its MVIDs resolve for real) plus a surface manifest recording exactly
    /// <paramref name="recorded"/>.</summary>
    private static string StageAppDirectory(IEnumerable<string> recorded)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-appdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "MeshWeaver.*.dll"))
            File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), overwrite: true);
        File.WriteAllText(
            Path.Combine(dir, FrameworkBuildIdentity.SurfaceManifestFileName),
            string.Join('\n', recorded.Select(n => $"{n}=surface-of-{n}")));
        return dir;
    }

    private static IEnumerable<string> ReferencedInDirectory(string directory, string simpleName)
    {
        var candidate = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return [];
        using var stream = File.OpenRead(candidate);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
    }

    private static string? MvidInDirectory(string directory, string simpleName)
    {
        var candidate = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return null;
        using var stream = File.OpenRead(candidate);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
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

    /// <summary>
    /// A project's transitive COMPILE closure by simple name, read from the csproj graph.
    /// <para>Two details are load-bearing rather than tidiness: XML comments are stripped first (a
    /// commented-out ProjectReference is not a reference — samples/Northwind/MeshWeaver.Northwind.Domain
    /// carries one), and a reference marked <c>ReferenceOutputAssembly="false"</c> is skipped, because
    /// it contributes no compile reference and therefore no surface-manifest line. Both would
    /// otherwise make this walk claim a host records an assembly it does not.</para>
    /// </summary>
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
            var text = Regex.Replace(File.ReadAllText(project), "<!--.*?-->", string.Empty,
                RegexOptions.Singleline);
            foreach (Match element in Regex.Matches(text, "<ProjectReference\\b(?<attrs>[^>]*)>",
                         RegexOptions.Singleline))
            {
                var attrs = element.Groups["attrs"].Value;
                if (Regex.IsMatch(attrs, "ReferenceOutputAssembly\\s*=\\s*\"false\"",
                        RegexOptions.IgnoreCase))
                    continue;
                var include = Regex.Match(attrs, "Include\\s*=\\s*\"(?<path>[^\"]+)\"");
                if (!include.Success)
                    continue;
                stack.Push(Path.GetFullPath(
                    Path.Combine(directory, include.Groups["path"].Value.Replace('\\', '/'))));
            }
        }
        return names;
    }

    /// <summary>Every project that opts into a surface manifest — i.e. every host that RESOLVES the
    /// framework build identity and must therefore agree with the others about it.</summary>
    private static IReadOnlyList<string> SurfaceManifestHosts(string repositoryRoot) =>
        // 🚨 The exclusions match the path RELATIVE to the root. Filtering the absolute path threw
        // every file away when the checkout itself lives under .worktrees/<name> — a sibling git
        // worktree, which is how every session of this repo works — and the test then "passed" over
        // an empty host set. The HaveCountGreaterThan(1) assertion above is what caught it; keep
        // both, since a discovery that silently finds nothing is the failure mode here.
        [.. Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(repositoryRoot, p)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is ".worktrees" or "bin" or "obj" or "node_modules"))
            .Where(p => Regex.IsMatch(
                Regex.Replace(File.ReadAllText(p), "<!--.*?-->", string.Empty, RegexOptions.Singleline),
                "<MeshWeaverSurfaceManifest>\\s*true\\s*</MeshWeaverSurfaceManifest>",
                RegexOptions.IgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)];
}
