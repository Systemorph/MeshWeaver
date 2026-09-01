#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>An adopted build must not be born DIRTY</b> (Systemorph/MeshWeaver#1834).
///
/// <para>Adoption asserts "these bytes correspond to the live source set" — the producer's own
/// source ticks are meaningless on the consumer (the bake writes zeros), so the consumer re-stamps
/// <see cref="NodeTypeDefinition.CompiledSources"/> from its own snapshot. It used to read that
/// snapshot off the node it was patching. But <c>PrebuiltAssemblySeeder.Seed</c> writes CROSS-HUB,
/// so its lambda runs against the MIRROR's snapshot — which predates the first-activation write of
/// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> that the seeder's own subscribe
/// TRIGGERS. For the sourceless fixture the older tests in this assembly use, both sides are empty
/// and nothing shows. For a type WITH sources it stamped <c>CompiledSources = null</c> under a
/// non-empty <c>CurrentSourceVersions</c> — i.e. <see cref="NodeTypeDefinition.IsDirty"/> — and
/// <c>InstallReleaseRequestWatcher</c>'s "satisfied by the existing current build" branch requires
/// <c>!IsDirty</c>. So the release request <c>PackageInstaller</c> issues one step after
/// <c>SeedPrebuiltAssemblies</c> recompiled the type that had just been adopted: not declined, not
/// logged as a failure, simply thrown away one step later.</para>
///
/// <para>Both halves are asserted here, because either alone is satisfiable by accident: the
/// STAMP (the two snapshots agree, so the record is honest) and the CONSEQUENCE (the very next
/// release request is answered by the existing build instead of dispatching a compile). The
/// consequence is read off the ONE write that decides it — the emission that first carries
/// <c>LastReleaseRequestHandledAt</c> at/after the trigger also carries the branch's verdict in
/// <c>CompilationStatus</c> (<c>Ok</c> = satisfied, <c>Pending</c> = dispatched), so there is no
/// wall clock and no scheduling luck in the assertion.</para>
///
/// <para>🚨 Deliberately NOT asserted on <see cref="MeshNode.Version"/>: it is a counter the owner
/// mints for EVERY writer, and this node demonstrably has several (#1833 flaked on exactly that
/// proxy). Every assertion below reads a field the code under test WROTE.</para>
/// </summary>
public class AdoptedBuildSourceStampTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly System.Collections.Generic.List<string> refusals = [];

    [Fact(Timeout = 180_000)]
    public async Task AdoptedTypeWithSources_IsNotDirty_AndTheNextReleaseRequestIsSatisfied()
    {
        const string id = "StampedThing";
        var typePath = $"{TestPartition}/{id}";
        var sourcePath = $"{typePath}/Source/model";

        await CreateNodeType(id);
        await CreateSource(typePath, "model",
            "public record StampedThing { public string Title { get; init; } = string.Empty; }");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0xDE, 0xAD, 0xBE, 0xEF])));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the bundle names a type this mesh holds");

            // ── The STAMP ────────────────────────────────────────────────────────────────────
            // Wait for the state in which the question is even askable: the build is adopted AND
            // the owner has published its source snapshot. Both are fields the code under test
            // writes; neither is a counter or a clock.
            var adoptedDef = await AwaitDefinition(typePath,
                d => d.LatestAssemblyPath is { Length: > 0 } && d.CurrentSourceVersions is not null,
                "the adoption stamped an assembly and the owner published its source snapshot");

            adoptedDef.CurrentSourceVersions!.Keys.Should().Contain(sourcePath,
                "the fixture exists to exercise a type WITH sources — an empty snapshot on both "
                + "sides is exactly the case that hid this defect");
            adoptedDef.CompiledSources.Should().NotBeNull(
                "the adopted build's source snapshot is stamped by the OWNER, whose copy of "
                + "CurrentSourceVersions is authoritative — a cross-hub patch cannot see a field "
                + "the owner wrote after the mirror's snapshot");
            adoptedDef.CompiledSources!.Should().Equal(adoptedDef.CurrentSourceVersions!,
                "the stamp IS the live snapshot — equal by construction, not by timing");
            adoptedDef.IsDirty.Should().BeFalse(
                "an adopted build that reads dirty is recompiled by the next release request, "
                + "which defeats adoption entirely (#1834)");
            adoptedDef.RequestedSourceStampAt.Should().BeNull(
                "the stamp request is ONE-SHOT: a standing request could later re-stamp "
                + "CompiledSources over a compile's own snapshot and suppress a needed rebuild");

            // ── The CONSEQUENCE ──────────────────────────────────────────────────────────────
            // Exactly what PackageInstaller.RequestReleases does one step after seeding.
            var beforeRequest = DateTimeOffset.UtcNow;
            var requested = await Mesh.ObserveNodeTypeRelease(typePath,
                    onError: msg => refusals.Add(msg))
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            requested.Should().BeTrue(
                "the release trigger must land, or the assertion below is vacuous"
                + $" — refusals: {string.Join(" | ", refusals)}");

            var handled = await AwaitDefinition(typePath,
                d => d.LastReleaseRequestHandledAt is { } h && h >= beforeRequest,
                "the release-request watcher handled the trigger");

            // THE assertion. The watcher stamps LastReleaseRequestHandledAt in the SAME write as
            // its verdict, so this one emission says which branch ran.
            handled.CompilationStatus.Should().Be(CompilationStatus.Ok,
                "the release request must be SATISFIED by the adopted build — Pending here means "
                + "the watcher dispatched a compile of the type it had just adopted (#1834)");
            handled.LatestAssemblyPath.Should().Be(adoptedDef.LatestAssemblyPath,
                "a satisfied request produces no new build, so the adopted assembly pointer stands");
            handled.LastCompileSucceededAt.Should().Be(adoptedDef.LastCompileSucceededAt,
                "…and nothing re-stamped a compile that never ran");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// The OTHER ordering, forced rather than waited for: the owner has ALREADY published its
    /// source snapshot before the adoption's write lands (the case the issue's instrumentation
    /// caught as the "second adoption", <c>csv=0 cs=0</c>). Here the sources watcher's write
    /// cannot carry the stamp — nothing re-emits the sources query — so the standalone
    /// <c>InstallAdoptedSourceStampWatcher</c> is the only thing that can converge it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AdoptionAfterTheSnapshotIsAlreadyPublished_IsStillStamped()
    {
        const string id = "LateAdoptedThing";
        var typePath = $"{TestPartition}/{id}";
        var sourcePath = $"{typePath}/Source/model";

        await CreateNodeType(id);
        await CreateSource(typePath, "model",
            "public record LateAdoptedThing { public int Answer { get; init; } = 42; }");

        // Force the OTHER ordering: activate the owner FIRST with an unrelated write, so its
        // sources watcher has published CurrentSourceVersions before the adoption's patch is even
        // composed. (A cross-hub READ is served from the mirror and does not activate the owner —
        // a write does, which is exactly why the seeder's own write is what triggers the
        // first-activation publication it then races.)
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => Mesh.GetWorkspace().GetMeshNodeStream(typePath).Update(curr =>
                    curr.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        ? curr with { Content = d with { Description = "activated" } }
                        : curr))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await(TestContext.Current.CancellationToken);

        var published = await AwaitDefinition(typePath,
            d => d.CurrentSourceVersions is { } csv && csv.ContainsKey(sourcePath),
            "the owner published its source snapshot before anything was adopted");
        published.CompiledSources.Should().BeNull("nothing has been adopted or compiled yet");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0x01, 0x02, 0x03])));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1);

            var def = await AwaitDefinition(typePath,
                d => d.LatestAssemblyPath is { Length: > 0 } && d.RequestedSourceStampAt is null
                     && d.CompiledSources is not null,
                "the standalone stamp watcher converged the adoption");
            def.CompiledSources!.Should().Equal(def.CurrentSourceVersions!);
            def.IsDirty.Should().BeFalse();
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  #2813 — THE THREE-WAY, END TO END.
    //
    //  The comparison itself is unit-tested in MeshWeaver.Graph.Test (AdoptedBuildProvenanceTest,
    //  a pure function over a staged definition). What those tests CANNOT see — and what let this
    //  mechanism ship armed and inert for months — is whether a real bundle's fingerprint ever
    //  reaches the comparison on a real mesh. It did not: both production callers bound to a
    //  convenience overload that hard-coded `sourceFingerprint: null`, so every adoption in every
    //  deployment took the legacy branch and the refusal could not fire. These three tests drive
    //  the WHOLE path — BundleWriter → BundleReader → ShippedPrebuiltBundles → Seed → the owning
    //  hub's stamp — and each of them fails if any link drops the value.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 <b>THE test for #2813.</b> The bundle states which sources its bytes were built from, and
    /// they are NOT the ones this mesh holds — the exact shape of the incident: a sync pulled new
    /// source for <c>Crm/Migration</c>, an assembly built from the PREVIOUS source was adopted over
    /// it, and the next run executed last week's code and destroyed four client documents.
    ///
    /// <para>Asserted on the ONE write that records the refusal, observed through a subscription
    /// armed BEFORE the seed — a later local compile supersedes that state within seconds, so
    /// reading the node afterwards would be a race whose green is meaningless.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ABundleBuiltFromDifferentSource_IsRefused()
    {
        const string id = "StaleThing";
        var typePath = $"{TestPartition}/{id}";

        await CreateNodeType(id);
        await CreateSource(typePath, "model",
            "public record StaleThing { public string Title { get; init; } = string.Empty; }");

        // What a producer that baked an OLDER revision of this file would have recorded. Computed
        // with the same public function the bake uses — the point is that it is a HONEST
        // fingerprint of DIFFERENT source, not a corrupt or random string, because a corrupt value
        // would be refused for reasons that say nothing about staleness.
        var staleFingerprint = FingerprintOf(
            $"{typePath}/Source/model",
            "public record StaleThing { public string Title { get; init; } = \"last week\"; }");

        var dir = CreateBundleDirectory();
        var refusal = ObserveDefinition(typePath,
            d => d.BuildProvenance is BuildProvenance.AdoptionRefused);
        using var subscription = refusal.Connect();
        try
        {
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0xDE, 0xAD, 0xBE, 0xEF]))
                {
                    SourceFingerprint = staleFingerprint,
                });

            await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);

            var refused = await refusal.Await(TestContext.Current.CancellationToken);

            refused.AdoptedSourceFingerprint.Should().Be(staleFingerprint,
                "the bundle's fingerprint must survive the whole transport — writer, reader, Seed "
                + "— or the comparison is made against nothing and silently degrades to "
                + "AdoptedUnverified, which is the state this fix exists to leave");
            refused.CurrentSourceFingerprint.Should().NotBeNullOrEmpty(
                "the owner must have computed its OWN fingerprint, or the three-way answered its "
                + "scariest branch on an absence rather than on a comparison");
            refused.CurrentSourceFingerprint.Should().NotBe(staleFingerprint,
                "the fixture is only meaningful if the two genuinely differ");
            refused.CompiledSources.Should().BeNull(
                "a refused adoption must not stamp CompiledSources — that write is exactly what "
                + "makes IsDirty false by construction and is precisely the lie that made every "
                + "health signal report green while stale bytes ran");
            refused.CompilationStatus.Should().Be(CompilationStatus.Pending,
                "the refusal must DRIVE a real local compile of the live source, not merely "
                + "record a complaint");
            refused.RequestedSourceStampAt.Should().BeNull(
                "the stamp request is consumed by the refusal so it cannot re-fire");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// The other side of the same gate, and the one whose failure would be an OUTAGE rather than a
    /// bug: a bundle whose fingerprint MATCHES must adopt, be marked verified, and NOT recompile.
    ///
    /// <para>If the producer's hash and the consumer's hash disagreed in any way — a serializer
    /// option, a node property, a shaping rule — every good bundle on every mesh would be refused
    /// and every portal would recompile everything on every boot. This test is the reason the
    /// fingerprint is taken over the compile INPUT (path + code text through
    /// <c>NodeCompileShaping.CollectCompileSources</c>) rather than over the source nodes'
    /// serialised content.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ABundleBuiltFromTheSameSource_AdoptsAsVerified_AndIsNotRecompiled()
    {
        const string id = "VerifiedThing";
        var typePath = $"{TestPartition}/{id}";
        const string code =
            "public record VerifiedThing { public int Answer { get; init; } = 42; }";

        await CreateNodeType(id);
        await CreateSource(typePath, "model", code);

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0x0F, 0xF1, 0xCE]))
                {
                    // Computed by the TEST, from the source text alone, exactly as a bake computes
                    // it from a checkout — never read back off the mesh, which would make the
                    // assertion circular and prove nothing about cross-process equality.
                    SourceFingerprint = FingerprintOf($"{typePath}/Source/model", code),
                });

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1, "the bundle names a type this mesh holds");

            var def = await AwaitDefinition(typePath,
                d => d.LatestAssemblyPath is { Length: > 0 }
                     && d.RequestedSourceStampAt is null
                     && d.CompiledSources is not null,
                "the adoption was stamped by the owner");

            def.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
                "the producer's fingerprint and the owner's own must be EQUAL for identical "
                + "source — if this is AdoptedUnverified the value never arrived, and if it is "
                + "AdoptionRefused the two producers hash the same content differently, which "
                + "would refuse every good bundle in the fleet");
            def.CompiledSources!.Should().Equal(def.CurrentSourceVersions!,
                "a verified adoption stamps the owner's live snapshot");
            def.IsDirty.Should().BeFalse(
                "a verified adoption is current by construction — reading dirty here means the "
                + "next release request recompiles the build that was just proven correct");
            def.CompilationStatus.Should().NotBe(CompilationStatus.Pending,
                "nothing about a VERIFIED adoption may dispatch a compile");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// The LEGACY path, asserted rather than assumed. A bundle from a producer that records no
    /// fingerprint must still adopt — as <see cref="BuildProvenance.AdoptedUnverified"/>.
    ///
    /// <para>🚨 Refusing on an absence is the tempting and wrong answer: it would make every
    /// legacy-bundle type dirty on arrival, so every install would recompile everything (the 43
    /// activations / 13.5 s of boot the prebuilt lane exists to remove), and on a
    /// <c>Modules:RequirePrebuilt</c> mesh — where a local compile is refused by design — it would
    /// PARK every such type: an outage with no recovery path, self-inflicted by a guard. The
    /// requirement for an unproven bundle is VISIBILITY, not refusal.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALegacyBundleWithNoFingerprint_StillAdopts_ButAsUnverified()
    {
        const string id = "LegacyThing";
        var typePath = $"{TestPartition}/{id}";

        await CreateNodeType(id);
        await CreateSource(typePath, "model",
            "public record LegacyThing { public string Note { get; init; } = string.Empty; }");

        var dir = CreateBundleDirectory();
        try
        {
            WriteBundle(
                Path.Combine(dir, "shipped.zip"),
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                // No SourceFingerprint — every bundle produced before #2813 looks exactly like this.
                new BundleWriter.AssemblyEntry(typePath, () => new MemoryStream([0x11, 0x22, 0x33])));

            var adopted = await ShippedPrebuiltBundles.SeedAll(Mesh, dir, null)
                .FirstAsync()
                .Await(TestContext.Current.CancellationToken);
            adopted.Should().Be(1);

            var def = await AwaitDefinition(typePath,
                d => d.LatestAssemblyPath is { Length: > 0 }
                     && d.RequestedSourceStampAt is null
                     && d.CompiledSources is not null,
                "the adoption was stamped by the owner");

            def.AdoptedSourceFingerprint.Should().BeNull("the bundle recorded none");
            def.BuildProvenance.Should().Be(BuildProvenance.AdoptedUnverified,
                "provenance is UNKNOWN, which is neither a clean bill of health nor a refusal");
            def.CompiledSources!.Should().Equal(def.CurrentSourceVersions!,
                "the stamp is KEPT deliberately — withholding it parks every legacy type on a "
                + "RequirePrebuilt mesh");
            def.CompilationStatus.Should().NotBe(CompilationStatus.Pending,
                "an unproven bundle is not a refused one — it must not dispatch a compile");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// What a PRODUCER computes for a one-file source set: the same public function the bake calls,
    /// over a node built the way the tree loader builds one. Deliberately NOT read back off the
    /// mesh — the whole claim under test is that a process holding only the source TEXT arrives at
    /// the value a mesh holding the imported NODE arrives at.
    /// </summary>
    private static string FingerprintOf(string sourcePath, string code)
        => NodeTypeSourceFingerprint.Compute(
            [
                new MeshNode(
                    sourcePath[(sourcePath.LastIndexOf('/') + 1)..],
                    sourcePath[..sourcePath.LastIndexOf('/')])
                {
                    NodeType = "Code",
                    State = MeshNodeState.Active,
                    Content = new CodeConfiguration { Code = code, Language = "csharp" },
                },
            ],
            sourcePath[..sourcePath.IndexOf("/Source/", StringComparison.Ordinal)]);

    /// <summary>
    /// A connectable stream of the FIRST definition emission matching <paramref name="predicate"/>,
    /// so a caller can ARM the observation before the write it is about to trigger. Waiting after
    /// the fact would miss any state a later write supersedes — and the refusal is exactly such a
    /// state, since it dispatches the compile that replaces it.
    /// </summary>
    private IConnectableObservable<NodeTypeDefinition> ObserveDefinition(
        string typePath, Func<NodeTypeDefinition, bool> predicate)
        => Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null && predicate(d))
            .Select(d => d!)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(90))
            .Replay(1);

    private async Task<NodeTypeDefinition> AwaitDefinition(
        string typePath, Func<NodeTypeDefinition, bool> predicate, string because)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                && predicate(d))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(TestContext.Current.CancellationToken);
        node.Should().NotBeNull(because);
        return node!.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;
    }

    private Task<MeshNode> CreateNodeType(string id)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => NodeFactory.CreateNode(new MeshNode(id, TestPartition)
                {
                    Name = id,
                    NodeType = MeshNode.NodeTypePath,
                    MainNode = $"{TestPartition}/{id}",
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition
                    {
                        Description = "adoption stamp fixture",
                        // 🚨 Pre-settled so the FIRST-BUILD KICKOFF (which fires on a
                        // CompilationStatus=null type the moment its hub activates) cannot race a
                        // real Roslyn compile against the adoption and stamp CompiledSources from
                        // it — that would mask the very defect under test. Everything the defect
                        // turns on is untouched: no CompiledSources, no CurrentSourceVersions, no
                        // assembly coordinates, i.e. exactly the freshly-imported shape.
                        CompilationStatus = CompilationStatus.Unavailable,
                    },
                }))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();
    }

    private Task<MeshNode> CreateSource(string typePath, string name, string code)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => NodeFactory.CreateNode(new MeshNode(name, $"{typePath}/Source")
                {
                    Name = name,
                    NodeType = "Code",
                    State = MeshNodeState.Active,
                    Content = new CodeConfiguration { Code = code, Language = "csharp" },
                }))
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();
    }

    private static string CreateBundleDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mw-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteBundle(
        string path, string frameworkMvid, params BundleWriter.AssemblyEntry[] entries)
    {
        using var file = File.Create(path);
        BundleWriter.Write(file, "fixture", "1.0.0", frameworkMvid, entries, "fixture-sha");
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort — the OS reclaims temp at reboot
        }
    }
}
