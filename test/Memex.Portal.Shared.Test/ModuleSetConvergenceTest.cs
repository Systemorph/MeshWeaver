using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Every replica of one mesh runs ONE module set (#3395).</b>
///
/// <para><b>The mechanism these pin.</b> Landing moves each module's activation entry
/// INDEPENDENTLY, the moment that module's bytes are on disk, and a process pins whatever the
/// entries said at ITS boot instant. Measured on memex-cloud 2026-09-06: three pods of one
/// ReplicaSet, one image (<c>3.0.0-rc9.ci.7693</c>), booted 11:33:39 / 11:41:04 / 12:51:21 around a
/// landing wave at 12:18–12:27, and <b>39 of 40 pinned generations differed</b> between the two
/// older pods and the newest. They share ONE NodeType node, so each stamped the module set IT
/// resolved and each read the other's stamp as stale: the pair ping-pongs recompiles, and where one
/// replica's set lacks a module the sources need, a healthy NodeType FAILS with no source change.
/// The issue was filed as "one boot, two module sets 15 s apart"; it is three replicas, 16 minutes
/// apart — a rolling replacement across a landing wave, not a process racing itself.</para>
///
/// <para><b>The fix these prove.</b> A landing wave no longer moves what the mesh RUNS. It stages
/// bytes, and when the whole wave is done it PROPOSES one immutable, sequenced set
/// (<see cref="ModuleSetStore.Propose"/>); boot loads the mesh's newest proposal
/// (<see cref="ModuleActivationBoot.ProjectOntoMeshSet"/>), never its own read of the moving
/// per-module entries. Two replicas booting at any two instants between two wave completions
/// therefore load identical bytes, and a boot mid-wave cannot observe a half-landed mix at all.</para>
///
/// <para><b>What makes each of these able to FAIL.</b> Every one drives the REAL
/// <see cref="ModuleLandingService"/> against a real temp volume and composes the boot exactly as
/// <c>MemexConfiguration.ConfigureMemexMesh</c> does — nothing is mocked and nothing re-derives the
/// rule locally. Neutralising the convergence (making
/// <see cref="ModuleActivationBoot.ProjectOntoMeshSet"/> return its input) flips
/// <see cref="ReplicasBootingAroundOneWave_LoadTheIdenticalModuleSet"/>,
/// <see cref="AReplicaBootingMidWave_NeverLoadsAHalfLandedMix"/> and
/// <see cref="AWaveThatNeverCompletes_LeavesTheMeshOnTheSetItWasOn"/> — the three that ARE the
/// divergence.</para>
/// </summary>
public class ModuleSetConvergenceTest : IDisposable
{
    private const string ModuleA = "MeshWeaver.Payments.Stripe";
    private const string ModuleB = "MeshWeaver.Social";
    private const string ModuleC = "MeshWeaver.Speech";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-moduleset-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    /// <summary>Creates the per-test deployment root and the REAL landing service over it.</summary>
    public ModuleSetConvergenceTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    // ───────────────────────────────────────────────────────────── the mechanism

    /// <summary>
    /// 🚨 <b>THE repro.</b> Three replicas boot across one landing wave — before it, in the middle
    /// of it, and after it — exactly the memex-cloud shape. All three must run the SAME generation
    /// of every module: the two that boot before the wave PROPOSES load the previous set, and the
    /// one that boots after loads the proposed one, so the mesh is on at most ONE set at a time
    /// and the replicas that share a set share it exactly.
    ///
    /// <para>Without the convergence the middle replica takes whatever half of the wave had landed
    /// by its boot instant — a THIRD set, distinct from both — which is the 39-of-40 divergence.</para>
    /// </summary>
    [Fact]
    public async Task ReplicasBootingAroundOneWave_LoadTheIdenticalModuleSet()
    {
        await LandWave(ModuleA, ModuleB, ModuleC);
        var first = BootReplica();

        // The second wave starts: ModuleA's bytes are down and its ENTRY has already moved, which
        // is the state the middle replica of the incident booted into.
        await Land(ModuleA);
        var second = BootReplica();

        // …and only now does the wave finish and propose.
        await Land(ModuleB);
        await Land(ModuleC);
        await ProposeWave();
        var third = BootReplica();

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        // The whole set moved together — a wave is all-or-nothing, never per module.
        Assert.All(first.Keys, name => Assert.NotEqual(first[name], third[name]));
    }

    /// <summary>
    /// A replica booting while a wave is landing must never see the TORN combination — some
    /// modules from the new wave, the rest from the old. That combination is one no wave ever
    /// produced and nothing was ever tested against; it is what makes a NodeType compile fail
    /// against a module set that "resolved fine fifteen seconds earlier".
    /// </summary>
    [Fact]
    public async Task AReplicaBootingMidWave_NeverLoadsAHalfLandedMix()
    {
        await LandWave(ModuleA, ModuleB, ModuleC);
        var before = BootReplica();

        await Land(ModuleA);
        var midWave = BootReplica();

        Assert.Equal(before[ModuleA], midWave[ModuleA]);
        Assert.Equal(before, midWave);
    }

    /// <summary>
    /// 🚨 <b>A wave that dies half-landed is a FAILURE, not drift.</b> The mesh stays on the set it
    /// was on — nothing half-landed is ever adopted by anybody — and the modules whose bytes DID
    /// land are named as stranded on every surface that reads the report. The old behaviour had no
    /// such state: those modules simply ran on whichever pods happened to boot after their own
    /// landing and not on the others, with every surface reporting Healthy.
    /// </summary>
    [Fact]
    public async Task AWaveThatNeverCompletes_LeavesTheMeshOnTheSetItWasOn()
    {
        await LandWave(ModuleA, ModuleB);
        var settled = BootReplica();

        // A wave lands a module the mesh has never carried, and dies — no ProposeWave().
        await Land(ModuleC);

        Assert.Equal(settled, BootReplica());

        var report = new PendingModuleActivations(root).Read(
            loadedAssemblyNames: settled.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            loadedModuleGenerations: settled);
        Assert.True(report.HasDeferred);
        Assert.Equal(ModuleC, Assert.Single(report.Deferred).Name);
        Assert.Contains("landed but are in NO proposed module set", report.Describe());
        // 🚨 And NOT reported as pending: "a restart activates this" would be a promise no restart
        // can keep, because boot loads the mesh's set and the stranded module is not in it.
        Assert.False(report.HasPending);
    }

    /// <summary>
    /// The convergence window, stated positively: it OPENS when a wave proposes a set nothing is
    /// serving yet and CLOSES at the first replica that boots onto it. That is what makes "the old
    /// replica is still serving the previous set" a known, bounded, visible state rather than
    /// silence — and a window that stays open names a wave whose bytes no replica runs.
    /// </summary>
    [Fact]
    public async Task TheConvergenceWindow_OpensOnTheProposalAndClosesOnTheFirstBoot()
    {
        await LandWave(ModuleA);
        BootReplica();
        Assert.False(ModuleSetStore.Read(root).ConvergencePending);

        await LandWave(ModuleA);
        var afterProposal = ModuleSetStore.Read(root);
        Assert.True(afterProposal.ConvergencePending);
        Assert.Contains("NO replica has booted onto it yet", ModuleSetStore.Describe(afterProposal));

        BootReplica();
        var afterRestart = ModuleSetStore.Read(root);
        Assert.False(afterRestart.ConvergencePending);
        Assert.Equal(afterProposal.Proposed!.Id, afterRestart.Current!.Id);
    }

    // ───────────────────────────────────────────────────────────── the store's own rules

    /// <summary>
    /// A wave that changed nothing proposes nothing, so sequences count waves that moved bytes
    /// rather than boots. Without this the auto-update reconcile — which runs at EVERY boot and
    /// normally lands nothing — would mint a fresh set on every restart and re-open the
    /// convergence window forever.
    /// </summary>
    [Fact]
    public async Task AWaveThatLandedNothing_ProposesNothing()
    {
        await LandWave(ModuleA, ModuleB);
        var first = ModuleSetStore.Read(root).Proposed!;

        Assert.Null(await ProposeWave());
        Assert.Equal(first.Sequence, ModuleSetStore.Read(root).Proposed!.Sequence);
    }

    /// <summary>
    /// 🚨 Two replicas can finish a wave at the same moment and each write sequence N+1. Both
    /// records survive — nothing here is ever renamed over a live file — so every reader must
    /// resolve the tie the SAME way without coordinating, and the conflict must be reported rather
    /// than absorbed. Nothing is lost either: the proposal is derived from the activation record,
    /// so the next wave carries everything both replicas landed.
    /// </summary>
    [Fact]
    public async Task TwoReplicasProposingOneSequence_ResolveToTheSameSetAndSaySo()
    {
        await LandWave(ModuleA);
        var landed = ModuleActivationSidecar.Read(root);

        // The second replica's wave also landed ModuleB, so its set for the SAME sequence differs.
        await Land(ModuleB);
        var rival = ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), "replica-2");
        Assert.NotNull(rival);
        WriteRivalProposalAtSameSequence(rival!.Sequence, landed);

        var reported = new List<string>();
        var index = ModuleSetStore.Read(root, reported.Add);

        Assert.Equal([rival.Sequence], index.ConflictingSequences);
        Assert.Contains(reported, m => m.Contains("proposed by more than one replica"));
        // Deterministic: the ordinally smallest id, so two replicas reading this pick one set.
        Assert.Equal(
            ProposalIds(rival.Sequence).OrderBy(id => id, StringComparer.Ordinal).First(),
            index.Proposed!.Id);
        Assert.Equal(index.Proposed.Id, ModuleSetStore.Read(root).Proposed!.Id);
    }

    /// <summary>
    /// 🚨 #3675: a sequence two replicas proposed is a decided outcome, not a record that could not
    /// be read. The three-argument <see cref="ModuleSetStore.Read(string, Action{string}, Action{string})"/>
    /// keeps the two apart — the notice never reaches the fault channel — while the two-argument
    /// overload still surfaces it to every caller that read it there before (the health report's
    /// notes, the boot log), so nothing that used to be said falls silent.
    /// </summary>
    [Fact]
    public async Task ADuplicateProposal_IsANotice_NeverAFault()
    {
        await LandWave(ModuleA);
        var landed = ModuleActivationSidecar.Read(root);
        await Land(ModuleB);
        var rival = ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), "replica-2")!;
        WriteRivalProposalAtSameSequence(rival.Sequence, landed);

        var faults = new List<string>();
        var notices = new List<string>();
        var index = ModuleSetStore.Read(root, faults.Add, notices.Add);

        Assert.Empty(faults);
        Assert.Contains(notices, m => m.Contains("proposed by more than one replica"));
        Assert.Equal([rival.Sequence], index.ConflictingSequences);

        // Compatibility: the two-argument overload still says it, where every caller heard it.
        var reported = new List<string>();
        ModuleSetStore.Read(root, reported.Add);
        Assert.Contains(reported, m => m.Contains("proposed by more than one replica"));

        // And a record that genuinely cannot be read IS a fault, on the three-argument overload too.
        // At the NEWEST sequence, so it is a record the reader has to open (#3676 opens only the
        // deciding records; an unreadable one below them is not consulted and not a fault).
        var garbage = Path.Combine(ModuleSetStore.SetsDirectory(root), $"{rival.Sequence + 1:D9}-garbage000000000.proposed.json");
        File.WriteAllText(garbage, "{ not json");
        faults.Clear();
        ModuleSetStore.Read(root, faults.Add, notices.Add);
        Assert.Contains(faults, m => m.Contains("garbage000000000"));
    }

    /// <summary>
    /// 🚨 #3675, the consequence that mattered: the GC used to count the duplicate-proposal notice as
    /// a read fault and fail closed on EVERY pass for as long as one such pair existed — on
    /// memex-cloud, 100 duplicate sequences kept 687 set records and 843 generation directories
    /// alive that no pass ever reclaimed, and reading that pile is what put the /health probe over
    /// its timeout. With a duplicate pair on the volume the sweep must still prune the records
    /// below the current set and still reclaim an orphan generation — and a record that really
    /// cannot be read must still stop it (#2509 is untouched).
    /// </summary>
    [Fact]
    public async Task GarbageCollection_StillSweepsWithADuplicateProposalOnTheVolume()
    {
        await LandWave(ModuleA);
        BootReplica();                                   // sequence 1 adopted — the mesh is on it
        var landed = ModuleActivationSidecar.Read(root);
        await Land(ModuleB);
        var rival = ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), "replica-2")!;
        WriteRivalProposalAtSameSequence(rival.Sequence, landed);
        BootReplica();                                   // the winner at sequence 2 is adopted
        var current = ModuleSetStore.Read(root).Current!.Sequence;
        Assert.Equal(rival.Sequence, current);

        var orphan = Path.Combine(root, "modules", "MeshWeaver.Orphan@deadbeef");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "MeshWeaver.Orphan.dll"), "not a module");
        var below = SetRecordsBelow(current);
        Assert.NotEmpty(below);

        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        Assert.False(Directory.Exists(orphan), "an unreferenced generation survived a pass that had nothing unreadable to fail closed on");
        Assert.Empty(SetRecordsBelow(current));
        Assert.Equal(2, ProposalIds(current).Count);   // the duplicate pair itself is never the one removed

        // #2509 still holds: one record that cannot be read, and the pass reclaims nothing.
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "MeshWeaver.Orphan.dll"), "not a module");
        File.WriteAllText(Path.Combine(ModuleSetStore.SetsDirectory(root), $"{current + 1:D9}-garbage000000000.proposed.json"), "{ not json");
        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));
        Assert.True(Directory.Exists(orphan), "a pass with an unreadable set record must not reclaim anything");
    }

    private IReadOnlyList<string> SetRecordsBelow(long sequence) =>
        [.. Directory.EnumerateFiles(ModuleSetStore.SetsDirectory(root), "*.json")
            .Select(f => Path.GetFileName(f))
            .Where(f => long.TryParse(f[..f.IndexOf('-')], out var seq) && seq < sequence)];

    /// <summary>
    /// 🚨 The generations the mesh's set PINS are referenced even when the activation entries have
    /// moved past them — otherwise the convergence would re-open the 2026-08-27 outage from the
    /// other side, with GC reclaiming the very bytes every replica is executing.
    /// </summary>
    [Fact]
    public async Task GarbageCollection_KeepsTheGenerationsTheMeshSetPins()
    {
        await LandWave(ModuleA);
        var pinned = ModuleSetStore.Read(root).Proposed!.Generations[ModuleA];

        // A wave lands a newer generation and has not proposed: the entry has moved off `pinned`,
        // so the activation record alone no longer references it — but every replica runs it.
        await Land(ModuleA);
        Assert.NotEqual(pinned, ModuleActivationSidecar.Read(root).Entries.Single(e => e.Name == ModuleA).Directory);

        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        Assert.True(Directory.Exists(Path.Combine(root, "modules", pinned)),
            "the generation the mesh's module set pins was reclaimed — every replica is running it");
    }

    /// <summary>
    /// 🚨 The one degradation, and it is REPORTED. If the generation the mesh's set pins is not on
    /// the volume — a pod on the PREVIOUS platform build sweeping by the entries alone during this
    /// change's own rollout, a manual deletion, a partial restore — the two candidates are "run the
    /// generation the entry names" and "run nothing", and running nothing is the WORSE half of
    /// #3395: a missing module is what turns a healthy NodeType into a failed one. So it falls back
    /// and says so, rather than handing boot's own existence gate a generation it would then skip.
    /// </summary>
    [Fact]
    public async Task WhenTheSetsGenerationIsGone_ItFallsBackToTheEntryAndSaysSo()
    {
        await LandWave(ModuleA);
        var pinned = ModuleSetStore.Read(root).Proposed!.Generations[ModuleA];

        // A newer generation lands and is not proposed; then the pinned one is reclaimed.
        await Land(ModuleA);
        Directory.Delete(Path.Combine(root, "modules", pinned), recursive: true);

        var degraded = new List<(string Module, string Reason)>();
        var projected = ModuleActivationBoot.ProjectOntoMeshSet(
            ModuleActivationSidecar.Read(root),
            ModuleSetStore.Read(root).Proposed,
            onDeferred: null,
            landedDllExists: entry => ModuleActivationBoot.LandedModuleDllExists(root, entry),
            onSetGenerationMissing: (module, reason) => degraded.Add((module, reason)));

        var entry = Assert.Single(projected.Entries);
        Assert.NotEqual(pinned, entry.Directory);
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, entry));
        Assert.Equal(ModuleA, Assert.Single(degraded).Module);
        Assert.Contains("whose bytes are NOT on the volume", degraded[0].Reason);
    }

    /// <summary>
    /// 🚨 A LEGACY fixed-folder entry — one with no <c>Directory</c>, resolving to
    /// <c>modules/&lt;name&gt;/</c> — names no generation, so <c>GenerationsOf</c> excludes it by
    /// design and the set can say nothing about it. Asking "does the set name it?" would therefore
    /// DEFER it, i.e. silently disable every such module on every deployment that still has one.
    /// It must pass through, exactly as a disabled entry does. (Copilot review, #3444.)
    /// </summary>
    [Fact]
    public async Task ALegacyFixedFolderEntry_IsNeverDeferredByTheMeshSet()
    {
        await LandWave(ModuleA);

        // The pre-generation shape: an entry with no Directory, bytes in modules/<name>/.
        Directory.CreateDirectory(Path.Combine(root, "modules", ModuleB));
        File.WriteAllBytes(Path.Combine(root, "modules", ModuleB, ModuleB + ".dll"), [0x4D, 0x5A]);
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry { Name = ModuleB });

        var deferred = new List<string>();
        var projected = ModuleActivationBoot.ProjectOntoMeshSet(
            ModuleActivationSidecar.Read(root),
            ModuleSetStore.Read(root).Proposed,
            (module, _) => deferred.Add(module),
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));

        Assert.Empty(deferred);
        var legacy = Assert.Single(projected.Entries, e => e.Name == ModuleB);
        Assert.Null(legacy.Directory);
        Assert.Contains(
            ModuleActivationBoot.ComputeEffectiveModuleEntries(
                baselineEntries: null, projected, ModulePlatformFloor.DeclineReason,
                entry => ModuleActivationBoot.LandedModuleDllExists(root, entry)),
            m => m.Landed?.Name == ModuleB);
    }

    /// <summary>
    /// 🚨 A deterministically-RESOLVED set conflict is a handled condition with a valid index
    /// behind it, not an absence of evidence. Folding the store's notes into
    /// <c>UndeterminedReason</c> would make the whole activation report — and the health check
    /// reading it — go unknown for a state every replica resolves identically. The notes belong on
    /// the mesh-set line. (Copilot review, #3444.)
    /// </summary>
    [Fact]
    public async Task AResolvedSetConflict_IsReportedWithoutMakingTheStateUndetermined()
    {
        await LandWave(ModuleA);
        var landed = ModuleActivationSidecar.Read(root);
        await Land(ModuleB);
        var rival = ModuleSetStore.Propose(root, ModuleActivationSidecar.Read(root), "replica-2")!;
        WriteRivalProposalAtSameSequence(rival.Sequence, landed);

        var report = new PendingModuleActivations(root).Read(
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            loadedModuleGenerations: ImmutableDictionary<string, string>.Empty);

        Assert.False(report.IsUndetermined);
        Assert.Contains("proposed by more than one replica", report.MeshModuleSet);
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>Lands one module through the REAL landing service — a fresh generation plus the
    /// activation entry, exactly as an auto-update or an install does.</summary>
    /// <remarks>
    /// 🚨 REAL assembly bytes, not a three-byte MZ stand-in. Since #3538 the landing MEASURES the
    /// module's link requirements against this platform's surface, so bytes that are not a managed
    /// assembly are refused — correctly, and this test is about generations, not about that gate.
    /// Any assembly whose references this process carries works; the packaging assembly is the
    /// same one <c>ServedModuleBytesTest</c> uses for the same reason.
    /// </remarks>
    private async Task Land(string name) =>
        await landing.LandModule(name, [(name + ".dll", RealAssemblyBytes)])
            .Timeout(TestTimeouts.Convergence).Await();

    /// <summary>A real, loadable managed assembly's bytes — see <see cref="Land"/>.</summary>
    private static byte[] RealAssemblyBytes =>
        File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location);

    /// <summary>Closes a landing wave — the coordination step that moves the mesh's set.</summary>
    private async Task<ModuleSet?> ProposeWave() =>
        await landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await();

    /// <summary>A COMPLETE wave: land every module, then propose once.</summary>
    private async Task LandWave(params string[] names)
    {
        foreach (var name in names)
            await Land(name);
        await ProposeWave();
    }

    /// <summary>
    /// One replica's boot, composed EXACTLY as <c>MemexConfiguration.ConfigureMemexMesh</c> does —
    /// read the record, read the mesh's sets, project, compute the union, record the adoption.
    /// Re-deriving any of it here would let this test agree with itself while the portal diverges.
    /// </summary>
    /// <returns>Module name → the generation directory this replica loaded it from.</returns>
    private ImmutableSortedDictionary<string, string> BootReplica()
    {
        var persisted = ModuleActivationSidecar.Read(root);
        var sets = ModuleSetStore.Read(root);
        var onMeshSet = ModuleActivationBoot.ProjectOntoMeshSet(
            persisted,
            sets.Proposed,
            onDeferred: null,
            landedDllExists: entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baselineEntries: null,
            onMeshSet,
            ModulePlatformFloor.DeclineReason,
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        if (sets.Proposed is { } adopted)
            ModuleSetStore.RecordAdoption(root, adopted, adoptedBy: "replica");
        return effective
            .Where(m => m.Landed is { Directory.Length: > 0 })
            .ToImmutableSortedDictionary(
                m => m.Landed!.Name, m => m.Landed!.Directory!, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions SetJson = new(JsonSerializerDefaults.Web);

    /// <summary>The ids of every proposal file recorded at one sequence — read off the volume, so
    /// the conflict test asserts on what is actually there rather than what it expected.</summary>
    private ImmutableList<string> ProposalIds(long sequence)
    {
        var prefix = sequence.ToString("D9") + "-";
        return [.. Directory
            .EnumerateFiles(ModuleSetStore.SetsDirectory(root), "*.proposed.json")
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => JsonSerializer.Deserialize<ModuleSet>(File.ReadAllText(f), SetJson)!.Id)];
    }

    /// <summary>
    /// Writes a SECOND proposal at <paramref name="sequence"/> from a different activation record —
    /// the two-replicas-one-sequence race, arranged rather than waited for.
    /// </summary>
    private void WriteRivalProposalAtSameSequence(long sequence, ModuleActivationList other)
    {
        var generations = ModuleSetStore.GenerationsOf(other);
        var set = new ModuleSet(sequence, ModuleSetStore.IdOf(generations), generations, DateTime.UtcNow, "replica-1");
        File.WriteAllText(
            Path.Combine(
                ModuleSetStore.SetsDirectory(root),
                $"{sequence:D9}-{set.Id[..Math.Min(16, set.Id.Length)]}.proposed.json"),
            JsonSerializer.Serialize(set, SetJson));
    }
}
