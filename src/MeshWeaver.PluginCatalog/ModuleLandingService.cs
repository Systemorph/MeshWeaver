using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The runtime writer into <c>modules/</c> (#1664 step 7) — the ONE code path that lands a
/// compiled module's assemblies beside the app at runtime and records its activation, so the next
/// restart loads it (restart-as-activation, #1664 step 8). Slice C's install funnel calls it
/// through <see cref="PluginBundleClient.AdoptModule"/> (bundle-fetch → MVID gate → here), from
/// the install orchestrator (<c>CatalogLayoutAreas.InstallOrUpdate</c>) and the boot reconcile
/// (<see cref="RegistryUpdateReconciler"/>).
///
/// <para><b>The MEASURED platform gate holds at placement (#3538, #3648).</b> Landing links the
/// module's entry assembly against the running platform's surface
/// (<see cref="MeshWeaver.Mesh.ModulePlatformLink"/>) from memory, before a byte reaches disk: an
/// unlinkable or unreadable module REFUSES the landing (the observable errors, naming the missing
/// type); declined bytes never reach disk. The module's declared <c>minMeshVersion</c> floor is
/// ADVISORY — <see cref="ModulePlatformFloor.DeclineReason(string?)"/> words it, the landing logs it
/// (Information) and records it on the entry, and it decides nothing. It used to be the FIRST gate
/// here, and that string comparison (<c>ci &lt; rc &lt; clean</c>) is what held every production
/// portal on the morning build for all of 2026-09-07 while the link probe would have loaded every
/// candidate. The framework MVID the bundle was built against is recorded and logged as
/// metadata — modules bind by simple name, and their contract is what the probe measures, not
/// build identity (that strict gate belongs to the NodeType bake lane).</para>
///
/// <para><b>…except on the SHELF lane, where an unlinkable module HOLDS instead of refusing.</b>
/// <see cref="ShelveModule"/> is the PUBLISH path's entry (the registry stocking its warehouse):
/// a warehouse may carry modules for platforms newer than itself, so bytes this process cannot
/// load land and their activation entry is recorded — the serve side lists them for consumers,
/// whose own landing measures them against THEIR platform, and this process's boot runs the same
/// probe (<c>MeshBuilder.InstallAssemblies</c>) and parks what it cannot load until a platform
/// update makes it loadable. There is deliberately NO persisted "held" flag — held-ness is
/// DERIVED from the bytes against the running platform at each decision point, so it can never
/// go stale when the platform moves. <see cref="LandModule"/> — the direct-adopt funnel — keeps
/// refusing: an instance must never hold bytes its own next boot would try to load into a
/// platform that lacks their types… which for the adopt path is the point of the install, so a
/// hold there would be a package whose binary half silently never arrives.</para>
///
/// <para><b>The shelf never moves its head backwards (#3996).</b> The publish endpoint can receive
/// the same module from more than one release lane. A slower, older build is still valid warehouse
/// stock, but arrival order is not version order: on 2026-09-11 Mail 1.7.0 landed first and a core
/// CD carrying 1.6.1 arrived eleven minutes later, moved the activation head, and a restart silently
/// un-shipped 1.7.0. Shelf landings therefore compare a known incoming version with the current
/// head through <see cref="NuGetVersionComparer"/>, and an older upload never displaces a head whose
/// bytes are PRESENT — whether or not that head links on this registry's own platform, because the
/// shelf warehouses modules for newer platforms and boot runs the fallback when the head does not
/// load here. The older upload is kept as the head's fallback generation when it is the better one
/// (loadable here first, then the higher version); direct adoption remains unchanged because
/// <see cref="ModuleUpdateDecision"/> already refuses unattended downgrades before it downloads a
/// byte. 🚨 The rule holds across REPLICAS too (#4026): a landing no longer decides against a
/// snapshot and replaces a shared file with its decision — it records its own facts
/// (<see cref="ModuleLandingRecord"/>) and the head is DERIVED from every record present, so two
/// publishes of one module reaching two replicas seconds apart fold to the same head on every
/// replica. See <c>Doc/Architecture/ModuleActivationHeadOwnership</c>.</para>
///
/// <para><b>The generation a landing displaces is KEPT, as the new entry's fallback (#3649).</b>
/// <see cref="ModuleActivationEntry.PreviousDirectory"/> names it, the GC references it, and boot
/// loads it when the head generation does not load on the running platform — so a shelved landing
/// built for a newer platform leaves the module RUNNING its previous version instead of absent,
/// and the next GC pass no longer reclaims the only bytes that load. Carried forward past a
/// displaced generation that is itself measured unloadable here, so two unloadable landings in a
/// row cannot push the loadable one out of reach. Cleared by an uninstall.</para>
///
/// <para><b>The same-identity trap-door is refused too.</b> <c>MeshBuilder.ResolveModulePath</c>
/// resolves <c>modules/&lt;name&gt;/&lt;name&gt;.dll</c> BEFORE the app folder, so landing a
/// module named after an APP-CLOSURE assembly (e.g. <c>MeshWeaver.Graph</c>) would silently
/// shadow the platform's own binary on the next boot. A module whose entry DLL name collides
/// with a file in the app's base directory is refused.</para>
///
/// <para><b>Atomic on disk.</b> Files are written into a staging folder and renamed into
/// <c>modules/&lt;name&gt;/</c>; a crash mid-landing leaves at worst an orphaned
/// <c>.staging-*</c> folder, never a half-written module the next boot would load.</para>
///
/// <para><b>Reactive surface, pooled IO.</b> Both operations return cold
/// <see cref="IObservable{T}"/>s whose file IO runs on this service's own cap-1
/// <see cref="IoPool"/> — the one sanctioned bounded-IO primitive — so concurrent landings
/// serialize without a hand-rolled gate, and nothing blocks a hub scheduler. Mesh-scoped
/// singleton: the pool dies with the mesh.</para>
///
/// <para><b>Cross-REPLICA safety is structural, not a gate (#2090).</b> The pool bounds one
/// process; <c>/data</c> is shared by every portal replica. So the activation record is one file
/// PER MODULE (<see cref="ModuleActivationSidecar"/>) and a landing writes only its own — two
/// replicas landing different modules share no path, cannot lose each other's entry, and never
/// contend for one file's SMB lease. And since #4026 the same holds one level down, for two
/// replicas landing the SAME module: each writes a record of its own landing, and nothing a
/// landing decides is written back over a file another replica may have just written.</para>
///
/// <para><b>…except a GC pass against a CONCURRENT landing (#2303).</b> A landing's two writes
/// (move the bytes, then <see cref="ModuleActivationSidecar.WriteEntry"/>) are not atomic across
/// replicas, so another replica's <see cref="CollectGarbage(string, Microsoft.Extensions.Logging.ILogger?, TimeSpan?, DateTime?, CancellationToken)"/> can observe the new generation
/// directory before the entry that claims it exists and delete it as "unreferenced" — leaving a
/// real activation entry pointing at nothing a moment later. See
/// <see cref="DefaultGarbageMinAge"/> for the race and the grace-period fix.</para>
/// </summary>
public sealed class ModuleLandingService : IDisposable
{
    /// <summary>
    /// The directory a landed module's bytes live in: the entry's GENERATION when it names one,
    /// else the legacy fixed folder <c>modules/&lt;name&gt;/</c>. Pure — the one resolution rule,
    /// shared by boot and the serving side.
    /// </summary>
    public static string ModuleDirectoryFor(
        string baseDirectory, string moduleName, ModuleActivationEntry? entry) =>
        Path.Combine(baseDirectory, "modules",
            string.IsNullOrWhiteSpace(entry?.Directory) ? moduleName : entry!.Directory!);

    /// <summary>
    /// How long an UNREFERENCED directory under <c>modules/</c> must sit before
    /// <see cref="CollectGarbage(string, Microsoft.Extensions.Logging.ILogger?, TimeSpan?, DateTime?, CancellationToken)"/> treats it as truly orphaned rather than the first half of a
    /// landing this replica has not seen the SECOND half of yet — the race behind #2303.
    ///
    /// <para>🚨 <b>The race.</b> A landing is two writes on the shared <c>/data</c> volume,
    /// deliberately ordered bytes-then-entry (<see cref="LandCore"/>): <c>Directory.Move</c> lands
    /// the generation directory, THEN <see cref="ModuleActivationSidecar.WriteEntry"/> records the
    /// pointer. Those two writes are adjacent in one synchronous call on the LANDING replica, but
    /// nothing serializes them against a GC pass running on ANOTHER replica at the same moment —
    /// this pool and the per-module sidecar file both bound a single process
    /// (<c>Cross-REPLICA safety is structural</c>, above), not a cross-process sequence. If a GC
    /// pass reads the sidecar in the gap between the other replica's two writes, the new
    /// generation is on disk but no entry references it YET — indistinguishable from a genuinely
    /// orphaned directory — and GC deletes it a moment before the landing's
    /// <c>WriteEntry</c> lands, pointing a real, enabled activation entry at bytes that no longer
    /// exist. Nothing throws anywhere: the landing reports success (its own two writes both
    /// succeeded), and the entry only reveals itself as unresolvable the next time something reads
    /// it — <see cref="ModuleActivationStatus.Unresolvable(ModuleActivationList,
    /// IReadOnlySet{string}, IReadOnlyDictionary{string, string}, Func{string, string},
    /// Func{ModuleActivationEntry, bool})"/>'s loud report, or a boot that skips
    /// the module outright. That is the exact shape #2303 reported for
    /// <c>MeshWeaver.Blazor.EntityViews</c>: an ACTIVATED entry whose landed assembly was gone,
    /// with no exception or stack frame naming why.</para>
    ///
    /// <para>The fix cannot be a lock — this design is deliberately lock-free across replicas. A
    /// grace period is the correct primitive instead: refusing to reclaim anything younger than the
    /// window costs a genuinely orphaned directory nothing (the very next GC pass that still finds
    /// it unreferenced, now past the window, collects it) and closes the race, because the two
    /// writes of a real landing are back-to-back with no I/O between them — the actual exposure is
    /// low-single-digit seconds even over a slow network volume, and this window is generous on
    /// top of that.</para>
    /// </summary>
    public static readonly TimeSpan DefaultGarbageMinAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Garbage collection over <c>modules/</c>: deletes generation directories
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>) no activation entry references, leftover
    /// <c>.staging-*</c>, <c>.trash-*</c> from an earlier pass's interrupted delete, and the
    /// retired <c>.pending-*</c> folders of the abandoned deferred-swap scheme — but (except for
    /// <c>.trash-*</c>, which is unreferenced by construction) only once they are older than
    /// <paramref name="minAge"/> (<see cref="DefaultGarbageMinAge"/>): see that constant for why an
    /// UNREFERENCED directory is not proof of an orphan (#2303). Never touches legacy
    /// <c>&lt;name&gt;/</c> folders — entries without a generation still resolve there.
    ///
    /// <para>🚨 <b>Fail-closed on an unreliable reference set (#2509).</b> The reference set comes
    /// from <see cref="ModuleActivationSidecar.Read"/>, which — correctly, for BOOT (#2189) — skips
    /// a per-module entry file it cannot read and keeps the rest. For GC that per-module resilience
    /// inverts into a hazard: a transient SMB read fault on ONE entry file makes that module's
    /// ACTIVE generation indistinguishable from an orphan, and a pass that trusted the incomplete
    /// set deleted the very bytes the entry references — the dangling activation entries #2509
    /// measured on both prods. So any read fault skips every generation delete this pass
    /// (transient <c>.staging-/.pending-/.trash-</c> folders still collect — nothing references
    /// those by design); a later pass re-reads and sweeps then. Unreadable is never unreferenced.</para>
    ///
    /// <para>🚨 <b>Removal is atomic per directory (#2509).</b> A generation is first renamed to a
    /// <c>.trash-*</c> sibling — one atomic rename, after which it no longer exists as far as
    /// resolution (<see cref="ModuleDirectoryFor"/>) is concerned — and only then recursively
    /// deleted. The old delete-in-place could fail PARTWAY (one locked file on SMB aborts the
    /// recursion) and its skip-on-locked catch then preserved a HALF-GUTTED generation: entry DLL
    /// present, lazily-loaded dependency DLLs gone — the <c>Could not load file or assembly
    /// 'OpenAI'</c> shape of the 2026-08-27 outage. With rename-first, a refused rename (held open
    /// on SMB) leaves the directory fully intact, and an interrupted delete leaves only a
    /// <c>.trash-*</c> folder a later pass finishes. There is no half-deleted state either way.</para>
    ///
    /// <para>🚨 <b>Housekeeping — never a readiness step (#2684).</b> On an Azure Files (CIFS)
    /// <c>/data</c> this is one SMB round-trip per file, which is MINUTES for a handful of
    /// orphaned generations — and it reclaims nothing the portal needs in order to SERVE. It used
    /// to run synchronously in the portal's boot path, before the host listened, so rollout time
    /// became a function of how much garbage the previous generation left on a network volume:
    /// the pass blew the 300 s startup probe, the kill could not land on a process parked in
    /// uninterruptible IO (<c>Dsl</c>, <c>wchan=wait_for_response</c>), and the roll looped. It now
    /// runs from <see cref="ModuleGenerationsGcHostedService"/> once <c>ApplicationStarted</c> has
    /// fired, on the file-system <c>IIoPool</c>, and observes <paramref name="cancellationToken"/>
    /// between directories so a mesh teardown is never parked behind a slow unlink. Nothing here
    /// gates <c>/health</c> or <c>/alive</c>.</para>
    /// </summary>
    /// <param name="baseDirectory">The deployment root whose <c>modules/</c> folder is swept.</param>
    /// <param name="logger">Diagnostics — every removal and every age-deferred skip is logged.</param>
    /// <param name="minAge">The grace period below which an unreferenced directory is left alone.
    /// Defaults to <see cref="DefaultGarbageMinAge"/>; a test seam otherwise.</param>
    /// <param name="nowUtc">The reference "now" the age check compares against. Defaults to
    /// <see cref="DateTime.UtcNow"/>; a test seam so the race and its fix are provable without a
    /// real sleep.</param>
    /// <param name="cancellationToken">Observed BETWEEN directories — the unit of atomic removal.
    /// A cancelled pass stops before its next rename and returns what it removed so far; whatever
    /// is left (an intact orphan, or a <c>.trash-*</c> whose delete did not finish) is a later
    /// pass's job by construction. This is the pool's token when the pass runs through
    /// <c>IIoPool</c>, so a mesh teardown cancels the sweep instead of waiting on it.</param>
    /// <returns>How many directories were removed — where "removed" means gone from the
    /// <c>modules/</c> namespace (a rename into <c>.trash-*</c> counts even when the final delete
    /// is finished by a later pass).</returns>
    public static int CollectGarbage(
        string baseDirectory, ILogger? logger = null, TimeSpan? minAge = null, DateTime? nowUtc = null,
        CancellationToken cancellationToken = default)
        => CollectGarbage(baseDirectory, logger, minAge, nowUtc, deleteDirectory: null, cancellationToken);

    /// <summary>The implementation behind <see cref="CollectGarbage(string, ILogger?, TimeSpan?,
    /// DateTime?, CancellationToken)"/> with the delete operation as a seam, so the interrupted-delete
    /// path — the one that used to leave a half-gutted generation — is provable in a test without a
    /// real SMB lock.</summary>
    internal static int CollectGarbage(
        string baseDirectory, ILogger? logger, TimeSpan? minAge, DateTime? nowUtc,
        Action<string>? deleteDirectory, CancellationToken cancellationToken = default)
    {
        var modulesRoot = Path.Combine(baseDirectory, "modules");
        if (!Directory.Exists(modulesRoot))
            return 0;
        var delete = deleteDirectory ?? (dir => Directory.Delete(dir, recursive: true));
        var readFaults = 0;
        void OnReadFault(string msg)
        {
            readFaults++;
            logger?.LogError("{Message}", msg);
        }
        // 🚨 #4026: TWO answers are referenced, not one. The DERIVED activation (head and fallback
        // computed from the landing records) is what every current image runs; the STORED entries
        // are what an image that predates the records reads as its whole answer — and a
        // rolled-back replica boots from them. Referencing only the derived set would reclaim the
        // bytes a rollback needs; referencing both keeps a superset of what either reads, so this
        // pass never deletes anything the pre-#4026 pass would have kept.
        //
        // 🚨 And the fallback is ranked PER PLATFORM (Copilot's review of #4427): a rolling update
        // has two images live on one volume, each ranking by its own link verdicts, so the derived
        // generations of EVERY platform a verdict exists for — and of the platform with none — are
        // referenced, never only this process's.
        var stored = ModuleActivationSidecar.ReadStored(baseDirectory, OnReadFault);
        var referenced = ModuleActivationSidecar.ReferencedGenerations(baseDirectory, stored, OnReadFault)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 🚨 #3649: an entry's PREVIOUS generation is referenced exactly like its head one. It is
        // the generation boot falls back to when the head does not load on this platform — for a
        // Store-only module the ONLY generation that runs — and reclaiming it is precisely how a
        // shelved landing built for a newer platform used to take a working module away.
        // (ReferencedGenerations includes every PREVIOUS generation as well as every head.)
        // 🚨 #3395: the activation entries are NOT the whole reference set any more. The mesh's
        // module set deliberately pins an OLDER generation than the entry while a wave's landings
        // wait to be proposed — and that older generation is what every running replica LOADED. A
        // sweep that trusted the entries alone would reclaim the bytes the whole mesh is executing:
        // the 2026-08-27 outage from the other side. Both retained sets count (the one the mesh is
        // on and the one it has proposed), and a set directory that cannot be read counts as a read
        // fault for the same fail-closed reason the entries do.
        // 🚨 #3675: a record that could not be READ is a fault; a sequence that two replicas
        // proposed is a NOTICE — decided deterministically, nothing lost. The store used to deliver
        // both through one callback, so a single duplicate pair on the volume made this pass fail
        // closed forever (memex-cloud, 2026-09-08: 100 such sequences, 687 records, 843 generation
        // directories, never one reclaimed). Only the fault channel counts here.
        var setIndex = ModuleSetStore.Read(baseDirectory,
            onCorrupt: msg =>
            {
                readFaults++;
                logger?.LogError("{Message}", msg);
            },
            onNotice: msg => logger?.LogInformation("Modules GC: {Message}", msg));
        foreach (var generation in ModuleSetStore.ReferencedGenerations(setIndex))
            referenced.Add(generation);
        // Superseded set records are housekeeping like the rest of this pass — and only ever below
        // the CURRENT set, so a record whose generations are still in the reference set above is
        // never the one removed. Skipped entirely when anything was unreadable, for the same
        // fail-closed reason the generation deletes are.
        if (readFaults == 0 && setIndex.Current is { } onSet)
        {
            var prunedSets = ModuleSetStore.Prune(baseDirectory, onSet.Sequence,
                msg => logger?.LogDebug("{Message}", msg));
            if (prunedSets > 0)
                logger?.LogInformation(
                    "Modules GC: removed {Count} superseded module set record(s) below sequence "
                    + "{Sequence}", prunedSets, onSet.Sequence);
        }
        // 🚨 #3656: and the LOSER of a decided duplicate, at ANY sequence — Prune above only
        // reaches below the current set, so a duplicate at the newest sequence survived every
        // pass and every pod re-read it and re-reported the same decided conflict on every boot,
        // forever. Retiring it says the conflict once, where it is decided, and leaves the one
        // record the mesh actually resolves to. Gated on the same fail-closed counter as the
        // pruning above: with anything unreadable on the volume this pass removes nothing.
        if (readFaults == 0)
            ModuleSetStore.PruneDuplicateProposals(baseDirectory,
                onRetired: msg => logger?.LogInformation("Modules GC: {Message}", msg),
                onWarn: msg => logger?.LogWarning("Modules GC: {Message}", msg));
        // 🚨 #2509: with any entry file unreadable, `referenced` is INCOMPLETE — an ACTIVE
        // generation would read as an orphan. Generation deletes are skipped wholesale this pass.
        var referencesReliable = readFaults == 0;
        var reportedUnreliable = false;
        var cutoff = (nowUtc ?? DateTime.UtcNow) - (minAge ?? DefaultGarbageMinAge);
        // 🚨 #4026: the landing records are retired here too, and by a rule that cannot change
        // what Read answers — a record goes only when the entry derived without it is identical
        // (ModuleActivationSidecar.PruneLandingRecords). Behind the same fail-closed counter and
        // the same grace window as the generation deletes: a record younger than the window may be
        // a landing on another replica, and with anything unreadable nothing is retired.
        if (referencesReliable)
        {
            var retired = 0;
            foreach (var moduleName in ModuleActivationSidecar.ModuleNamesWithLandingRecords(baseDirectory))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                retired += ModuleActivationSidecar.PruneLandingRecords(
                    baseDirectory, moduleName,
                    stored.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase)),
                    cutoff,
                    msg => logger?.LogInformation("Modules GC: {Message}", msg));
            }
            retired += ModuleActivationSidecar.PruneLandingTemps(baseDirectory, cutoff);
            if (retired > 0)
                logger?.LogInformation(
                    "Modules GC: retired {Count} landing record file(s) the derived activation no "
                    + "longer needs", retired);
        }
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(modulesRoot))
        {
            // 🚨 Between directories, never mid-removal: a rename is atomic, and a .trash-* whose
            // delete was cut short is exactly the state a later pass already finishes (#2509). The
            // token is the IIoPool's (#2684) — a teardown drain must not wait out a slow unlink.
            if (cancellationToken.IsCancellationRequested)
            {
                logger?.LogInformation(
                    "Modules GC: cancelled after removing {Removed} directory(ies) — the rest is a "
                    + "later pass's job.", removed);
                break;
            }
            var leaf = Path.GetFileName(dir);
            var isTrash = leaf.StartsWith(".trash-", StringComparison.OrdinalIgnoreCase);
            var isTransient = isTrash
                || leaf.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase)
                || leaf.StartsWith(".pending-", StringComparison.OrdinalIgnoreCase);
            var isOrphanGeneration = !isTransient && leaf.Contains('@') && !referenced.Contains(leaf);
            if (!isTransient && !isOrphanGeneration)
                continue;
            if (isOrphanGeneration && !referencesReliable)
            {
                if (!reportedUnreliable)
                    logger?.LogWarning(
                        "Modules GC: {Faults} activation entry or set record file(s) could not be "
                        + "read, so the reference set is incomplete — SKIPPING every generation "
                        + "delete this pass (unreadable is never unreferenced, #2509). A later pass "
                        + "re-reads and sweeps.", readFaults);
                reportedUnreliable = true;
                continue;
            }
            // 🚨 #2303: an unreferenced directory younger than the grace window may simply be a
            // landing this replica has not seen the ACTIVATION ENTRY for yet — see
            // DefaultGarbageMinAge. Left for a later pass, which re-reads the sidecar and either
            // finds the entry now present (survives, correctly) or is still unreferenced and past
            // the window (a genuine orphan, collected then). `.trash-*` is exempt: it exists only
            // as the second half of a removal this or an earlier pass already committed.
            if (!isTrash && Directory.GetLastWriteTimeUtc(dir) > cutoff)
            {
                logger?.LogDebug(
                    "Modules GC: {Dir} is unreferenced but younger than the {MinAge} grace period "
                    + "— left in case a concurrent landing's activation entry has not landed yet.",
                    leaf, minAge ?? DefaultGarbageMinAge);
                continue;
            }
            var target = dir;
            if (isOrphanGeneration)
            {
                // Atomic removal (#2509): one rename takes the generation out of the modules/
                // namespace; a refusal (held open on SMB) leaves it FULLY intact for a later pass.
                var trash = Path.Combine(modulesRoot,
                    $".trash-{leaf}-{Guid.NewGuid():N}"[..(".trash-".Length + leaf.Length + 9)]);
                try
                {
                    Directory.Move(dir, trash);
                    removed++;
                    logger?.LogInformation("Modules GC: removed {Dir}", leaf);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Held open by a still-running pod — intact, a later pass collects it.
                    logger?.LogDebug("Modules GC: {Dir} is in use, skipped ({Reason})", leaf, e.Message);
                    continue;
                }
                target = trash;
            }
            try
            {
                delete(target);
                if (!isOrphanGeneration)
                {
                    removed++;
                    logger?.LogInformation("Modules GC: removed {Dir}", leaf);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An interrupted delete of a .trash-*/staging folder is harmless — nothing resolves
                // or loads it — and a later pass finishes the job.
                logger?.LogDebug(
                    "Modules GC: delete of {Target} did not finish ({Reason}) — a later pass "
                    + "collects the remainder.", Path.GetFileName(target), e.Message);
            }
        }
        return removed;
    }

    // Cap 1 deliberately: landing writes files and the cap IS the in-process serialization — no
    // lock, no semaphore outside the sealed IoPool primitive. Landing is rare and short; 1 costs
    // nothing. 🚨 It is NOT what makes the activation record safe: this pool bounds ONE process,
    // and /data is shared by every replica. Cross-process safety comes from the record's SHAPE —
    // one file per module (ModuleActivationSidecar), and one immutable record per landing within
    // it (#4026), so concurrent writers never decide through a path they both write.
    private readonly IoPool pool = new(1);
    private readonly string baseDirectory;
    private readonly ILogger<ModuleLandingService>? logger;
    private readonly Subject<Unit> activationChanged = new();

    // 🚨 Every EMISSION goes through the synchronized façade, never `activationChanged` directly.
    // An Rx Subject is not safe for concurrent OnNext/OnCompleted: its observer list can be observed
    // mid-mutation, which tears delivery. The two callers here genuinely can overlap — a landing
    // announces on whichever thread the pool result lands on, while Dispose completes on the
    // teardown thread — so this is a real race, not a theoretical one. Subject.Synchronize is Rx's
    // own answer and stays inside the reactive model (no lock of ours, nothing hand-woven); it is
    // the same wrapper MeshNodeStreamCache, ThreadInboxChannel and this project's own
    // ModuleDiscoveryService already use. Subscribe still goes to the subject itself — the
    // synchronization is only needed on the write side. (Copilot review, #2437.)
    private readonly ISubject<Unit> announce;

    /// <summary>
    /// Fires once each time THIS process changes the persisted activation record — a landing, a
    /// shelving, or a removal. The announcement half of restart-as-activation (#1979).
    ///
    /// <para><b>Why it has to exist for the reader to be usable.</b>
    /// <see cref="PendingModuleActivations"/> answers "is a restart pending for this package?" by
    /// reading the record on demand, which is correct and always current — but a view has to know
    /// WHEN to ask again. On the install path the module lands strictly AFTER the install record
    /// node is written (the content install completes, then the bundle is fetched and landed), so
    /// the node-driven re-render that flips a card to "installed" happens BEFORE the restart is
    /// pending. Without this signal the one moment the buyer is looking at the card is exactly the
    /// moment it cannot say so, and the note appears only on some later, unrelated render.</para>
    ///
    /// <para>Announcing a write so its readers can react is the same discipline the mesh applies to
    /// storage writes (#817/#824) — not a poll and not a watchdog: it fires on the write, or not at
    /// all. It is deliberately a bare signal rather than the new state, because the state is
    /// per-process and derived; every subscriber re-derives it from the record and the assemblies it
    /// has actually loaded, so two surfaces can never disagree.</para>
    ///
    /// <para>🚨 Emitted AFTER the pool work item completes, never inside it. This service's pool is
    /// cap-1, so a subscriber that reacted by asking this service to read would queue behind the
    /// very work item that notified it.</para>
    /// </summary>
    public IObservable<Unit> ActivationChanged => activationChanged;

    private readonly Subject<ModuleSet> moduleSetProposed = new();
    private readonly ISubject<ModuleSet> announceProposed;

    /// <summary>
    /// Fires once each time THIS process ENDS a landing wave by proposing a module set that
    /// differs from the one already proposed (<see cref="ProposeModuleSet"/> returned a set) — the
    /// signal that "the mesh's module set moved, and a restart is what adopts it" (#3650).
    ///
    /// <para>Distinct from <see cref="ActivationChanged"/> on purpose. That one fires per module,
    /// mid-wave; a restart taken on it would boot the replicas onto the set proposed BEFORE the
    /// wave (a landed-but-unproposed entry is deferred by
    /// <see cref="ModuleActivationBoot.ProjectOntoMeshSet"/>, #3395) and the wave's own proposal
    /// would then wait for the next unrelated restart. The self-updater treats this signal as a
    /// roll of the running image, subject to the same pacing floor as any other roll.</para>
    ///
    /// <para>Never fires for an idempotent proposal (a wave that changed nothing), so a boot
    /// reconcile against an up-to-date registry restarts nobody. Emitted AFTER the pool work item
    /// completes, like <see cref="ActivationChanged"/>, through a synchronized façade.</para>
    /// </summary>
    public IObservable<ModuleSet> ModuleSetProposed => moduleSetProposed;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Diagnostics — every landing, refusal and removal is logged.</param>
    /// <param name="baseDirectory">Seam for tests: the deployment root the <c>modules/</c>
    /// folder lives under. Defaults to <c>AppContext.BaseDirectory</c>.</param>
    public ModuleLandingService(
        ILogger<ModuleLandingService>? logger = null,
        string? baseDirectory = null)
    {
        this.logger = logger;
        this.baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        announce = Subject.Synchronize(activationChanged);
        announceProposed = Subject.Synchronize(moduleSetProposed);
    }

    /// <summary>
    /// The #4026 pin's constructor (InternalsVisibleTo): <paramref name="beforeRecording"/> runs on
    /// the landing's own thread once its bytes are on the volume and before it records anything —
    /// the window in which another replica's landing of the same module used to be lost. A test
    /// runs a second replica's whole landing inside it, which makes the interleaving that lost
    /// Mail 1.7.0 deterministic instead of a timing accident.
    /// </summary>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="baseDirectory">The deployment root.</param>
    /// <param name="beforeRecording">Runs once a landing's bytes are on the volume, before it records.</param>
    /// <param name="beforeProjecting">Runs once a landing or an uninstall has decided, before it writes
    /// the per-module file older images read — the window Copilot's review of #4427 named, in which
    /// a projection derived before another replica's uninstall (or landing) can be written after it.</param>
    /// <param name="platformIdentity">The platform build this replica runs — what its link verdicts
    /// are measured against and read back for. Production uses the live framework identity.</param>
    /// <param name="platformSurface">The platform half of the link probe's surface, given the probe
    /// directories. Production uses the running process; a test stands up a replica on ANOTHER image
    /// by handing it a surface built from that image's files.</param>
    internal ModuleLandingService(
        ILogger<ModuleLandingService>? logger, string? baseDirectory, Action<string>? beforeRecording,
        Action<string>? beforeProjecting = null, string? platformIdentity = null,
        Func<string[], ModulePlatformSurface>? platformSurface = null)
        : this(logger, baseDirectory)
    {
        this.beforeRecording = beforeRecording;
        this.beforeProjecting = beforeProjecting;
        if (platformIdentity is not null)
            platform = () => platformIdentity;
        if (platformSurface is not null)
            this.platformSurface = platformSurface;
    }

    /// <summary>Null in production — see the internal constructor.</summary>
    private readonly Action<string>? beforeRecording;

    /// <summary>Null in production — see the internal constructor.</summary>
    private readonly Action<string>? beforeProjecting;

    /// <summary>
    /// The platform build this replica runs — the key its link verdicts are recorded under and read
    /// back for (#4026, Copilot's review of #4427). The live framework identity in production,
    /// resolved on first use.
    /// </summary>
    private readonly Func<string?> platform = ModuleActivationSidecar.LivePlatform;

    /// <summary>The running process in production — see the internal constructor.</summary>
    private readonly Func<string[], ModulePlatformSurface> platformSurface =
        directories => ModulePlatformSurface.OfRunningProcess(directories);

    /// <summary>The deployment root the <c>modules/</c> tree lives under — exposed so the serving
    /// side (<see cref="ModuleBundleSource"/> callers) reads the SAME tree this service writes,
    /// tests included.</summary>
    public string BaseDirectory => baseDirectory;

    /// <summary>
    /// Lands a module: measures its link requirements against this platform, writes the assemblies
    /// atomically into <c>modules/&lt;name&gt;/</c>, and appends/updates the activation entry in the
    /// <c>modules/activation.json</c> sidecar with <c>PendingRestart = true</c>. The module
    /// LOADS on the next restart (restart-as-activation) — nothing is loaded into the running
    /// process.
    ///
    /// <para>Cold: nothing happens until Subscribe. Errors (refusals) surface on the
    /// observable.</para>
    /// </summary>
    /// <param name="name">The module name — its entry DLL name without extension.</param>
    /// <param name="assemblies">The module's closure: file name + bytes per assembly. Must
    /// contain the entry <c>&lt;name&gt;.dll</c>.</param>
    /// <param name="frameworkMvid">The framework MVID (MeshWeaver.Graph's ModuleVersionId) the
    /// assemblies were built against, as recorded by the producer — DIAGNOSTIC metadata: logged
    /// and recorded on the activation entry, never a refusal (modules bind by simple name; the
    /// strict MVID gate belongs to the NodeType bake lane).</param>
    /// <param name="packagePath">The install record's mesh path, when the store lane calls.</param>
    /// <param name="version">The package version the bundle was served at — recorded on the
    /// activation entry so the auto-update reconcile can answer "already landed" without a
    /// download (<see cref="ModuleActivationEntry.Version"/>).</param>
    /// <param name="minMeshVersion">The module's declared platform FLOOR — ADVISORY (#3648):
    /// recorded on the activation entry and logged, naming both versions when it ranks above the
    /// running platform (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>), never a
    /// refusal. The ONE gate is the measured one (<see cref="MeshWeaver.Mesh.ModulePlatformLink"/>,
    /// #3538): the module's actual link requirements against this platform's surface, which
    /// refuses bytes this process could not load whatever the floor says — and lands bytes it can
    /// load whatever the floor says.</param>
    /// <param name="sourceCommit">The producing repository's commit the bundle was built from, as
    /// the producer recorded it in the bundle manifest (#4158) — recorded on the activation entry
    /// and printed by <see cref="ModuleLoadReport"/>. DIAGNOSTIC, and never inferred: null means the
    /// producer stated none, which prints as an explicit "(unrecorded)".</param>
    /// <param name="nativeAssets">🚨 The module's RID-specific NATIVE payloads (#4126), each at
    /// <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> — the EXACT four segments
    /// <c>ModuleNativeAssets</c> composes its probe from, enforced here by
    /// <see cref="NuGetPackageWriter.IsModuleNativeLayout"/> rather than trusted. A payload at any
    /// other shape would be written to disk, read as shipped and never be looked at: the one
    /// outcome carrying natives exists to prevent, so it is refused instead of landed.</param>
    public IObservable<Unit> LandModule(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid = null,
        string? packagePath = null,
        string? version = null,
        string? minMeshVersion = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets = null,
        string? sourceCommit = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? nativeAssets = null)
        => pool.InvokeBlocking(_ =>
        {
            LandCore(name, assemblies, frameworkMvid, packagePath, version, minMeshVersion,
                staticAssets, sourceCommit, nativeAssets,
                holdUnloadable: false, keepNewerHead: false);
            return Unit.Default;
        })
        .Do(_ => AnnounceActivationChanged());

    /// <summary>
    /// Lands a module onto the REGISTRY SHELF (2026-08-22) — the publish path's entry, identical to
    /// <see cref="LandModule"/> in every rule but one: a module the link probe says THIS process
    /// cannot load lands its bytes and records the activation entry as HELD instead of refusing.
    ///
    /// <para><b>Why the two paths must differ.</b> The landing serves two roles: adopting a module
    /// for THIS runtime (the install funnel — the refusal is exactly right there, declined bytes
    /// must never reach a disk the next boot loads from), and STOCKING the registry's shelf (the
    /// publish endpoint). Applying the adopt rule to the shelf produced a three-way deadlock,
    /// measured in production 2026-08-22, when the gate was still the declared floor: modules
    /// extracted from the platform image declared <c>minMeshVersion: 3.0.0-rc7</c>, the registry
    /// ran rc6 and 409'd every upload — while its own <c>Modules:Required</c> gate held the rc7
    /// rollout for exactly those absent modules. The image doesn't ship them → only the registry
    /// can deliver them → the registry refuses to even CARRY them until it updates → it can't
    /// update without them. The declared floor itself decides nothing since #3648; the same
    /// deadlock shape is still possible for bytes the registry genuinely cannot link, and the
    /// shelf is what breaks it.</para>
    ///
    /// <para><b>What "held" means mechanically — no new state, the existing probe IS the hold.</b>
    /// The bytes go into a generation directory and the entry is recorded (enabled, floor
    /// included) exactly as for an active landing, so the serve side lists and serves them to
    /// consumers, whose own landing measures them against THEIR platform. This process's boot
    /// runs the same measurement in <c>MeshBuilder.InstallAssemblies</c> and parks what it cannot
    /// load (<see cref="MeshWeaver.Mesh.IncompatibleModule"/>) — and loads it, on that same normal
    /// path, at the first boot whose platform carries the types it needs (a platform update IS a
    /// restart, so no separate reconcile is needed). The one deliberate difference in the record:
    /// <c>PendingRestart</c> is NOT raised for a held landing — a restart cannot activate it, and
    /// a "restart required" no restart can clear is a false prompt.</para>
    ///
    /// <para>Every other refusal is unchanged — in particular the app-closure same-identity
    /// trap-door still refuses even in shelf mode, because a held module DOES load eventually and
    /// would shadow the platform binary then. Cold; the outcome says whether the landing was held
    /// and why, so the publish endpoint can tell its caller "shelved, will serve" apart from
    /// "activated here".</para>
    ///
    /// <para>🚨 <b>The second difference (#3996): the HEAD is the highest version the shelf holds,
    /// never the last upload to arrive.</b> An upload whose version ranks strictly BELOW the
    /// landed head's lands its bytes WITHOUT moving the head pointer — <c>shelf-only</c> — and is
    /// kept as the entry's fallback generation when it is the better one; the outcome says whether
    /// it was (<see cref="ModuleLandingOutcome.RetainedAsFallback"/>). The adopt path
    /// (<see cref="LandModule"/>) deliberately does NOT carry the rule: there, an older version is
    /// an operator who asked for it through the Store, and the unattended lane that could arrive at
    /// one by accident already refuses it upstream (<see cref="ModuleUpdateAction.SkipOlder"/>).
    /// On the publish route there is no operator: the caller is CI, and since #3461 two lanes push
    /// the same module — core CD's <c>plugins-bake</c> at the gate's Plugins sha, and the Plugins
    /// repo's own publish-bake — so the head was decided by whichever upload FINISHED last. Core CD
    /// runs take 40–50 minutes, so an older gate routinely landed after a newer publish. Measured on
    /// memex.meshweaver.cloud 2026-09-11: <c>MeshWeaver.Mail.MicrosoftGraph</c> 1.7.0 landed at
    /// 02:49Z and 1.6.1 displaced it at 03:00Z, so the next restart would have silently un-shipped
    /// the merge. See <c>Doc/Architecture/ModuleAdoptionPolicy</c>, "Never roll back unattended".</para>
    /// </summary>
    public IObservable<ModuleLandingOutcome> ShelveModule(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid = null,
        string? packagePath = null,
        string? version = null,
        string? minMeshVersion = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets = null,
        string? sourceCommit = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? nativeAssets = null)
        => pool.InvokeBlocking(_ =>
            LandCore(name, assemblies, frameworkMvid, packagePath, version, minMeshVersion,
                staticAssets, sourceCommit, nativeAssets,
                holdUnloadable: true, keepNewerHead: true))
            .Do(_ => AnnounceActivationChanged());

    /// <summary>
    /// Validates one module-relative asset path: forward slashes, no rooting, no traversal, and
    /// every segment a legal file name. These strings become PATHS UNDER the module folder, so a
    /// segment that escapes it is a write anywhere the process can reach — refused here, before
    /// any byte touches disk, exactly like the flat assembly names.
    /// </summary>
    internal static void ValidateAssetPath(string? value, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('\\')
            || value.StartsWith('/')
            || Path.IsPathRooted(value))
            throw new ArgumentException(
                $"Module '{moduleName}': '{value}' is not a valid module-relative asset path.");
        foreach (var segment in value.Split('/'))
            ValidateFileName(segment, $"asset path segment of module '{moduleName}'");
    }

    /// <summary>
    /// Validates one module-relative NATIVE path (#4126): everything
    /// <see cref="ValidateAssetPath"/> requires, PLUS the exact layout the module loader probes —
    /// <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>, four segments, no more and no fewer
    /// (<see cref="NuGetPackageWriter.IsModuleNativeLayout"/>, the one spelling the derivation,
    /// the packer and the bundle reader also use).
    ///
    /// <para>🚨 The layout is checked HERE and not only at the bundle boundary, because this is
    /// the method that puts the bytes on disk and every producer reaches it — the publish
    /// endpoint, a consumer's adopt, and a caller inside the process. A payload at any other
    /// shape lands, is never probed (<c>ModuleNativeAssets.CandidatePaths</c> composes exactly
    /// those four segments and has no recursive walk), and reads as shipped while behaving as
    /// absent: bytes on disk with nothing anywhere to grep. Refused, so that the one thing a
    /// native section cannot do is fail silently.</para>
    /// </summary>
    internal static void ValidateNativePath(string? value, string moduleName)
    {
        ValidateAssetPath(value, moduleName);
        if (!NuGetPackageWriter.IsModuleNativeLayout(value))
            throw new ArgumentException(
                $"Module '{moduleName}': '{value}' is not the layout the module loader probes "
                + "(exactly runtimes/<rid>/native/<file>). Landing it would write bytes to a path "
                + "nothing ever looks at — shipped in appearance, absent in behaviour. A native "
                + "that has to live elsewhere rides FLAT beside the entry assembly instead, which "
                + "is the loader's last probe.");
    }

    /// <summary>
    /// Reads the current activation list on this service's IO pool — the runtime counterpart of the
    /// boot-time <see cref="ModuleActivationSidecar.Read"/>, serialized behind the same cap-1 pool
    /// as the writes so a read never observes a landing halfway through its read-modify-write.
    /// </summary>
    public IObservable<ModuleActivationList> GetActivation()
        => pool.InvokeBlocking(_ => ModuleActivationSidecar.ReadFor(baseDirectory,
            msg => logger?.LogError("{Message}", msg), platform));

    /// <summary>
    /// Proposes the module set the deployment's activation record now describes — the coordination
    /// step that ENDS a landing wave (#3395), and the only thing that moves what the mesh runs.
    ///
    /// <para>🚨 <b>Call it when the WAVE is done, never after each module.</b> A wave lands its
    /// modules one at a time; proposing per module would publish every intermediate combination as
    /// a set the next boot could adopt, which is the torn half-landed mix this whole mechanism
    /// exists to make unreachable. A wave that dies half-landed simply never proposes: the mesh
    /// keeps running the set it was on, nothing half-landed is ever adopted, and the modules it did
    /// land report as landed-but-unproposed
    /// (<see cref="ModuleActivationBoot.ProjectOntoMeshSet"/>) — a named, visible failure rather
    /// than drift.</para>
    ///
    /// <para>Idempotent: a wave that landed nothing new derives the set that is already proposed
    /// and writes nothing, so sequences count waves that changed something rather than boots.
    /// Runs on this service's cap-1 pool, so it never observes a landing halfway through.
    /// Cold — nothing happens until Subscribe.</para>
    /// </summary>
    /// <returns>The set that was proposed, or null when the wave changed nothing.</returns>
    public IObservable<ModuleSet?> ProposeModuleSet()
        => pool.InvokeBlocking(_ =>
        {
            var landed = ModuleActivationSidecar.ReadFor(baseDirectory,
                msg => logger?.LogError("{Message}", msg), platform);
            var proposed = ModuleSetStore.Propose(baseDirectory, landed,
                proposedBy: Environment.MachineName,
                onCorrupt: msg => logger?.LogWarning("{Message}", msg));
            if (proposed is not null)
                logger?.LogInformation(
                    "[ModuleSet] landing wave complete — the mesh's module set is now {Sequence} "
                    + "('{Id}', {Count} module(s)). Replicas adopt it as they restart.",
                    proposed.Sequence, proposed.Id, proposed.Generations.Count);
            return proposed;
        })
        // Off the pool work item (cap-1: a subscriber that asked this service to read would queue
        // behind the very item that notified it) — and only for a set that actually moved.
        .Do(proposed =>
        {
            if (proposed is not null)
                AnnounceModuleSetProposed(proposed);
        });

    /// <summary>
    /// Uninstalls a module landed by <see cref="LandModule"/>: disables its activation entry
    /// (kept, for history and idempotence), deletes <c>modules/&lt;name&gt;/</c>, and sets
    /// <c>PendingRestart = true</c>. Takes effect at the next restart. Refuses a name the
    /// sidecar does not know — the appsettings-baseline module folders (laid out by publish)
    /// are not this service's to delete.
    /// </summary>
    public IObservable<Unit> RemoveModule(string name)
        => pool.InvokeBlocking(_ =>
        {
            RemoveCore(name);
            return Unit.Default;
        })
        .Do(_ => AnnounceActivationChanged());

    // Internal for the #4026 pin (InternalsVisibleTo): a second REPLICA's landing runs to
    // completion inside the first one's recording window, on the calling thread — a replica is
    // another process, and its pool is not this one's.
    internal ModuleLandingOutcome LandCore(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid,
        string? packagePath,
        string? version,
        string? minMeshVersion,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets,
        string? sourceCommit,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? nativeAssets,
        bool holdUnloadable,
        bool keepNewerHead)
    {
        foreach (var (relativePath, _) in staticAssets ?? [])
            ValidateAssetPath(relativePath, name);
        foreach (var (relativePath, _) in nativeAssets ?? [])
            ValidateNativePath(relativePath, name);
        ValidateFileName(name, "module name");
        if (assemblies is not { Count: > 0 })
            throw new ArgumentException($"Module '{name}': no assemblies to land.", nameof(assemblies));
        foreach (var (fileName, bytes) in assemblies)
        {
            ValidateFileName(fileName, $"assembly file of module '{name}'");
            if (bytes is not { Length: > 0 })
                throw new ArgumentException(
                    $"Module '{name}': assembly '{fileName}' has no bytes.", nameof(assemblies));
        }
        var entryDll = name + ".dll";
        if (!assemblies.Any(a => string.Equals(a.FileName, entryDll, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(
                $"Module '{name}': the assembly list does not contain its entry '{entryDll}' — "
                + "such a folder could never load.", nameof(assemblies));

        // 🚨 THE PLATFORM GATE, at placement — MEASURED, never declared (#3538, #3648).
        // `minMeshVersion` is a CLAIM its author writes; the module's real requirement is the SET
        // OF TYPES its bytes are linked against, which its own metadata states exactly. The claim
        // has been wrong in both directions within one week: memex-cloud (core of 09-03) adopted a
        // MeshWeaver.Graph.Views compiled on 09-06 against a Mesh.Contract carrying
        // `CodeOutputCurrency` (added 09-04) because the declared floor `3.0.0-rc8` was SATISFIED
        // by the running `3.0.0-rc9.ci.7693` — every render of every code cell then threw
        // TypeLoadException until a human read a pod log (#3538); and on 2026-09-07 the same
        // comparison (`ci < rc < clean`) REFUSED every module carrying an rc or 3.0.0 floor on
        // every ci-built portal, holding the fleet on the morning build while every one of those
        // modules would have linked. So the floor is ADVISORY — logged below, recorded on the
        // entry, deciding nothing — and the bytes are MEASURED against this platform's surface,
        // from memory, BEFORE anything touches the disk: a refusal here costs no generation
        // directory and leaves the previous generation running. The verdict is tri-state and
        // `MayLoad` is true for Linkable ALONE — a check that could not be made parks the module
        // exactly like one that failed. Deliberately NOT MVID equality either: modules bind by
        // simple name; the strict MVID gate is bake semantics and stays with the NodeType lane.
        //
        // On the ADOPT path a refusal is always safe and landing on faith is not: the missing type
        // would surface only at the next boot, with nothing connecting it to the install that
        // caused it. On the SHELF path (holdUnloadable — the publish endpoint, see ShelveModule)
        // the same verdict HOLDS instead of refusing: the bytes land for CONSUMERS, whose own
        // landing measures them against their platforms, while this process's boot runs the same
        // probe and parks what it records here.
        if (ModulePlatformFloor.DeclineReason(minMeshVersion) is { } advisory)
            logger?.LogInformation(
                "Module '{Name}' declares platform ≥ {Floor}; this deployment runs {Running} — "
                + "advisory (#3648), the link probe decides at placement: {Advisory}",
                name, minMeshVersion, ModulePlatformFloor.RunningVersion ?? "(unknown)", advisory);

        // Read ONCE: the surface below is measured against the landed set, and the previous
        // generation (#3649) is taken from the same read, so the two cannot disagree about which
        // generation this module currently has.
        var landedBefore = ModuleActivationSidecar.ReadFor(baseDirectory,
            msg => logger?.LogWarning("{Message}", msg), platform);
        var surface = PlatformSurface(landedBefore);
        var linkVerdict = LinkVerdict();
        var held = linkVerdict.MayLoad ? null : linkVerdict.Report();
        if (held is not null && !holdUnloadable)
        {
            logger?.LogWarning("Module '{Name}' REFUSED at landing: {Reason}", name, held);
            // 🚨 The refusal is RECORDED where a person looks (#4083), not only logged: the
            // module's own refusal marker, which the activation report reads onto the package
            // card and /health as "held: references YamlDotNet 18.1.0.0, platform provides
            // 16.3.0.0". Nothing landed, so there is no entry to carry it — the marker is the
            // record. Best-effort: a marker that cannot be written must not turn a refusal into
            // a different failure.
            try
            {
                ModuleActivationSidecar.SetRefused(baseDirectory, name, new ModuleActivationSidecar.RefusedLanding(
                    version, packagePath, linkVerdict.HoldSummary()!, DateTimeOffset.UtcNow,
                    linkVerdict.Needs(), linkVerdict.Provides()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(e, "Module '{Name}': the refusal marker could not be written", name);
            }
            throw new InvalidOperationException($"Module '{name}' refused: {held}");
        }

        ModuleLinkVerdict LinkVerdict()
        {
            var entryBytes = assemblies.First(a =>
                string.Equals(a.FileName, entryDll, StringComparison.OrdinalIgnoreCase)).Bytes;
            // The module's own closure travels WITH it, so a reference into it is not this gate's
            // question — the two were built together — UNLESS the platform carries the same
            // simple name and its copy is what binds (#4083); the probe decides which.
            var closure = assemblies
                .Select(a => Path.GetFileNameWithoutExtension(a.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return ModulePlatformLink.Check(entryBytes, name, closure, surface);
        }

        // 🚨 THE GENERATION IS CONTENT-ADDRESSED (#3656) — `name@<16 hex of SHA-256 over the bytes
        // this landing writes>`, never a random id. The leaf used to be `name@<8 random hex>`, and
        // that is what turned a HARMLESS simultaneity into a permanent divergence. Every replica
        // reconciles the same feed at boot and on every ModulePublished broadcast, so two replicas
        // deciding to land the SAME bundle at the same moment is the normal shape, not a rarity —
        // and each wrote the identical bytes into a DIFFERENT directory. The set each then derived
        // from the activation record (ModuleSetStore.GenerationsOf reads the DIRECTORY) therefore
        // named a different generation for that module, so both proposed the same sequence with
        // different ids: the conflict ModuleSetIndex resolves deterministically, the loser's bytes
        // left behind as an orphan, and the loser's `.proposed.json` sitting at the mesh's newest
        // sequence until some later wave superseded it — re-read and re-reported by every pod's
        // sweep in the meantime, on every boot, forever. Measured on memex-cloud 2026-09-08: 100
        // duplicate sequences, 687 set records, 843 generation directories.
        //
        // Addressing the directory by its CONTENT makes those two landings ONE landing: same leaf,
        // same activation entry, same derived set — and the second replica's Propose is the no-op
        // it should always have been, so no conflicting record is ever written. A GENUINE conflict
        // (two replicas landing different content) still derives two sets and is still reported;
        // that report is now about something that actually differs.
        var generation = $"{name}@{GenerationIdOf(assemblies, staticAssets, nativeAssets)}";

        // The head BEFORE this landing, exactly as THIS replica read it — for the report and the
        // restart signal only. 🚨 It DECIDES NOTHING any more (#4026): a decision taken against
        // this snapshot and written back seconds later, by an unconditional rename of a file every
        // replica shares, is how two replicas landing one module lost each other's landing. The
        // decision is DERIVED after this landing's record is on the volume, from every record
        // present (ModuleActivationSidecar.DeriveEntry), so it is the same on every replica
        // whatever order the writes landed in.
        var displaced = landedBefore.Entries.FirstOrDefault(e =>
            e.Enabled
            && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(e.Directory));

        // 🚨 #3996 — ON THE SHELF THE HEAD IS THE HIGHEST VERSION HELD, NEVER THE LAST TO ARRIVE.
        //
        // A publish makes bytes AVAILABLE; it does not, by itself, choose what the registry serves.
        // The head pointer used to move for every accepted upload, so which version a registry
        // served — and which one its own next restart loaded — was decided by CI queue timing.
        // Since #3461 two lanes push the SAME module (core CD's plugins-bake at the gate's Plugins
        // sha, and the Plugins repo's own publish-bake), and core CD runs take 40–50 minutes, so
        // the older gate routinely finished AFTER the newer publish. Measured on
        // memex.meshweaver.cloud 2026-09-11: MeshWeaver.Mail.MicrosoftGraph 1.7.0 landed 02:49Z
        // (237,568 B) and 1.6.1 displaced it at 03:00Z (231,424 B); the 03:30Z activation entry read
        // `Version=1.6.1 PreviousVersion=1.7.0`, i.e. the next restart silently un-shipped a merged
        // change. MeshWeaver.AI showed the same shape the same night.
        //
        // The rule is applied by the DERIVATION's replay (KeepsHead), and this landing contributes
        // the one fact it needs: its LANE (ModuleLandingRecord.YieldsToNewerHead — true here on the
        // shelf, false on the adopt path, where an older version is an operator who asked for it).
        // What each case does, deliberately:
        //   • STRICTLY OLDER  → shelf-only. The bytes land (the publisher's job succeeded — its
        //     artifact is on the shelf, and it competes for the fallback), the head stays. A
        //     DELIBERATE ROLLBACK therefore cannot be expressed by re-publishing an older version:
        //     publish the fixed build under a HIGHER version (roll forward), or uninstall the module
        //     on this registry first (RemoveModule disables it and removes its landing records, so
        //     the next publish is a first landing). The same trade SkipOlder makes on the consumer
        //     side, worth making because the publish route has no operator to mean a rollback.
        //   • EQUAL VERSION   → the head MOVES. Strictly-below, never at-or-below: a module's
        //     version encodes CONTENT only, so a rebuild of unchanged source against a new platform
        //     republishes under the SAME version and is exactly the artifact consumers are waiting
        //     for (Plugins#931/#723). Identical bytes resolve to the generation already recorded,
        //     so they are the no-op they should be.
        //   • PRE-RELEASE     → SemVer order, from the same comparer: 1.7.0 outranks 1.7.0-rc1, and
        //     3.0.0-ci.3758 outranks 3.0.0-ci.900 (numerically, which is the whole reason that type
        //     exists).
        //   • UNKNOWN VERSION on either side → the head MOVES. An unrecorded or non-SemVer version is
        //     absence of evidence, not evidence of olderness (rule R2 of
        //     Doc/Architecture/ModuleAdoptionPolicy).
        //   • A NEWER HEAD WHOSE BYTES ARE GONE → the head MOVES. Only a LANDED generation is
        //     protected; protecting a record that names missing bytes would make the rule a
        //     self-sealing outage. A re-publish of the head's OWN bytes is not this case: it
        //     resolves to the head's own directory, the landing restores the files that directory
        //     lost, and the head keeps its (higher) label.
        //   • A NEWER HEAD THAT DOES NOT LINK HERE → the head STAYS, deliberately. The shelf carries
        //     modules for platforms NEWER than the registry serving them, and boot runs the fallback
        //     when the head does not load here (#3649, rule R1). Loadability HERE is the FALLBACK's
        //     question — the derivation ranks the fallback by it, never the head.

        // 🚨 The same-identity trap-door: modules/<name>/<name>.dll wins over the app folder in
        // ResolveModulePath, so a module named after an app-closure assembly would shadow the
        // platform's own binary on the next boot.
        if (File.Exists(Path.Combine(baseDirectory, entryDll)))
            throw new InvalidOperationException(
                $"Module '{name}' refused: '{entryDll}' is part of the application closure, and "
                + $"modules/{name}/ would SHADOW it at the next boot "
                + "(ResolveModulePath probes the modules folder first).");

        // 🚨 GENERATIONS, NEVER SWAPS. Every landing writes a FRESH directory and moves the
        // activation pointer; nothing on this path ever deletes or overwrites a directory a
        // running pod may hold open. The delete-based swap could not be made safe on a shared
        // volume: an open file refuses deletion on SMB (the 409s), and the boot-time deferred
        // apply raced the OTHER pods of a rolling restart — deletes half-succeeded and 13 of 15
        // module closures were reduced to their entry DLL (2026-08-20). Old generations are
        // garbage-collected by the post-start GC pass (ModuleGenerationsGcHostedService →
        // CollectGarbage), skip-on-locked, once no entry references them.
        var modulesRoot = Path.Combine(baseDirectory, "modules");
        var target = Path.Combine(modulesRoot, generation);
        var staging = Path.Combine(modulesRoot, $".staging-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (fileName, bytes) in assemblies)
                File.WriteAllBytes(Path.Combine(staging, fileName), bytes);
            // Static web assets keep their RELATIVE path — a view pack's components request
            // _content/<pack>/leaflet/leaflet.js, and the host's module asset provider serves
            // <module folder>/wwwroot, so the shape has to survive the trip intact.
            foreach (var (relativePath, bytes) in staticAssets ?? [])
            {
                var destination = Path.Combine(
                    staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            // 🚨 NATIVE payloads keep their relative path for a harder reason than the assets do
            // (#4126): the path IS the contract. ModuleNativeAssets composes its probe from
            // exactly runtimes/<rid>/native/<file> under the module folder and has no recursive
            // walk, so a byte written anywhere else is a module that loads, reports nothing, and
            // throws DllNotFoundException at its first P/Invoke. The shape was refused above, at
            // the top of this method, before anything touched the disk.
            foreach (var (relativePath, bytes) in nativeAssets ?? [])
            {
                var destination = Path.Combine(
                    staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            // 🚨 An EXISTING target is the right answer, never a collision (#3656): the leaf is the
            // content address, so a directory already carrying this name already carries these
            // bytes. Another replica landing the same bundle concurrently, or an earlier landing
            // this deployment's entry has since moved off — either way the landing ADOPTS it
            // instead of writing a second copy under a second name, which is the whole point of
            // addressing it by content. Probed first and caught as well: the other replica's
            // rename can land between the probe and ours.
            var alreadyLanded = Directory.Exists(target);
            if (!alreadyLanded)
            {
                try
                {
                    Directory.Move(staging, target);
                }
                catch (IOException) when (Directory.Exists(target))
                {
                    alreadyLanded = true;
                }
            }
            if (alreadyLanded)
            {
                RestoreMissingFiles(staging, target, name, generation);
                Directory.Delete(staging, recursive: true);
                AdoptLandedGeneration(target, name, generation);
            }
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Best-effort staging cleanup — the original failure is what must surface.
            }
            throw;
        }

        // 🚨 The natives get a line of their OWN (#4126). Everything else about a native is
        // invisible until a P/Invoke throws — the module lands, loads, and reports nothing — so
        // "this landing wrote N engines, for these RIDs" is the only place a reader can tell a
        // module that shipped none from a lane that dropped them. Stated when there are some;
        // the absence of the line is not a claim, it is the ordinary case.
        if (nativeAssets is { Count: > 0 })
            logger?.LogInformation(
                "Module '{Name}': {Count} native payload(s) landed under modules/{Generation}/ — "
                + "{Paths}. The host resolves them at load time for ITS OWN rid "
                + "(ModuleNativeAssets, #1728); a rid this bundle does not carry falls back to the "
                + "runtime's own probing",
                name, nativeAssets.Count, generation,
                string.Join(", ", nativeAssets.Select(a => a.RelativePath)));

        // 🚨 #4026 — RECORD THE LANDING; NEVER DECIDE BY REPLACING A SHARED FILE.
        //
        // The landing's facts go into a file of their own, named by their content address, and the
        // head is DERIVED afterwards from every record on the volume. Two replicas landing
        // DIFFERENT content of this module write different files, so neither can lose the other's
        // landing; two landing the SAME bundle write one name with one content, the benign
        // collision #3656 already relies on for the generation directory. Nothing is decided against
        // a snapshot and written back later, which is the lost update #3996's rule could not see
        // across replicas (Mail 1.7.0 and 1.6.1 reaching two replicas seconds apart).
        beforeRecording?.Invoke(name);
        var record = new ModuleLandingRecord
        {
            Name = name,
            Source = ModuleActivationSources.Store,
            PackagePath = packagePath,
            Directory = generation,
            Version = version,
            FrameworkMvid = frameworkMvid,
            MinMeshVersion = minMeshVersion,
            SourceCommit = sourceCommit,
            YieldsToNewerHead = keepNewerHead,
        };
        void OnCorrupt(string message) => logger?.LogWarning("{Message}", message);
        // 🚨 What THIS platform measured about these bytes, recorded under THIS platform's identity
        // (Copilot's review of #4427). Loadability is a fact about the bytes AND the image, so a
        // replica on another image — or a later one on this image after a sibling landed — records
        // its own measurement instead of finding the first replica's frozen in the landing record.
        // Written before the landing record, so a record is never visible without its verdict.
        if (platform() is { Length: > 0 } measuredOn)
            ModuleActivationSidecar.WriteVerdict(baseDirectory, name, generation, measuredOn, held is null);
        var recorded = ModuleActivationSidecar.WriteLanding(baseDirectory, record);
        var state = ModuleActivationSidecar.ReadModuleHead(baseDirectory, name, OnCorrupt, platform);
        // The same bundle landed before, so its record was already here. Usually a no-op — but an
        // adopt landing re-installing the generation this deployment ran before (the Store's
        // rollback) must still take the head, so a landing that WOULD move it records a
        // re-arrival of its own instead of relying on a record that arrived earlier.
        if (!recorded
            && ModuleActivationSidecar.ReArrival(baseDirectory, state, record, DateTime.UtcNow, platform) is { } again)
        {
            ModuleActivationSidecar.WriteLanding(baseDirectory, again);
            state = ModuleActivationSidecar.ReadModuleHead(baseDirectory, name, OnCorrupt, platform);
        }
        var entry = state.Derived
            ?? throw new InvalidOperationException(
                $"Module '{name}': the landing record for {generation} is not on the volume after it "
                + "was written — a concurrent uninstall removed it. Nothing was activated; land it again.");

        // What this landing's report says, against the head as DERIVED — which on a quiet volume is
        // what the landing alone would have decided, and under a concurrent landing is the answer
        // every replica agrees on.
        var becameHead = string.Equals(entry.Directory, generation, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Version, version, StringComparison.Ordinal);
        var retainedAsFallback = !becameHead
            && string.Equals(entry.PreviousDirectory, generation, StringComparison.OrdinalIgnoreCase);
        var shelfOnly = becameHead ? null : ShelfOnlyReason(entry);

        string ShelfOnlyReason(ModuleActivationEntry head)
        {
            var ranksBelow = ModuleActivationSidecar.IsOrderableVersion(version)
                && ModuleActivationSidecar.IsOrderableVersion(head.Version)
                && NuGetVersionComparer.Instance.Compare(version, head.Version) < 0;
            var lead = ranksBelow
                ? $"version {version} ranks below {head.Version}, which this registry already holds as "
                  + $"the head generation {head.Directory} — the upload is SHELF-ONLY and the head does "
                  + "not regress (#3996). "
                : $"version {version ?? "(unversioned)"} landed, but {head.Version ?? "(unversioned)"} "
                  + $"({head.Directory}) arrived after it on this deployment's shared volume and is the "
                  + "head (#4026). ";
            var kept = string.Equals(head.PreviousDirectory, generation, StringComparison.OrdinalIgnoreCase)
                ? $"Its bytes are retained as the head's fallback generation and serve at version {version}. "
                : string.Equals(head.Directory, generation, StringComparison.OrdinalIgnoreCase)
                    ? "Its bytes are identical to the head's, so there is nothing to keep beside it. "
                    : $"Its bytes are NOT retained: the fallback slot keeps {head.PreviousVersion ?? "(unversioned)"} "
                      + $"({head.PreviousDirectory}), the better fallback by loadability here first and "
                      + "version second, so the modules GC reclaims this generation. ";
            return lead + kept + "To make these bytes the head, publish them under a higher version.";
        }

        // 🚨 THE PROJECTION, for images that predate the landing records. They read this module's
        // own file (activation.d/<Name>.json) as their whole answer, and a rolled-back replica boots
        // from it — so it is still written, now as the head this landing DERIVED, and only when it
        // differs (the file is shared by every replica, and a byte-identical rewrite is contention
        // with no reader). Two replicas may overwrite each other's projection, exactly as before;
        // for a module with records a current image never reads a head from it, so that lost
        // update now reaches only an older image, which had it anyway.
        //
        // 🚨 And it is marked as a PROJECTION (ProjectionOf), which is what keeps it from ever
        // deciding anything for a current image (Copilot's review of #4427): a landing that derived
        // "installed" before another replica's uninstall can still write this file after that
        // uninstall — re-reading first would not be a compare-and-swap — but a current image orders
        // the uninstall's TOMBSTONE against this landing's RECORD by arrival and never reads
        // installed-ness from a projection. An image that predates the records reads this file as
        // its whole answer, and for it the file stays last-writer-wins, as it always was.
        beforeProjecting?.Invoke(name);
        if (state.Stored is not { ProjectionOf: not null } stored
            || !entry.Equals(stored with { ProjectionOf = null }))
            ModuleActivationSidecar.WriteEntry(baseDirectory,
                entry with { ProjectionOf = ModuleActivationSidecar.LatestEventName(state) });
        // Bytes landed (head or shelf), so a refusal marker from an earlier landing of this module
        // no longer describes the state (#4083).
        ModuleActivationSidecar.ClearRefused(baseDirectory, name);
        // A HELD landing does not raise the restart signal: a restart cannot activate it (boot runs
        // the same link probe on the same bytes and parks the entry again), so "restart required"
        // would be a prompt no restart can clear. The platform update that DOES carry the types
        // is itself a restart, which activates the entry with no flag. 🚨 Neither does a SHELF-ONLY
        // landing (#3996): the head did not move, so a restart would load exactly what this process
        // is already running — the same false prompt from the other direction. The ONE exception:
        // a shelf-only landing that MOVED the fallback while the head does not load here — measured
        // by the link probe now, or by the boot that already failed to load it (its unloadable
        // marker, which a static probe cannot see). Boot runs the fallback then, so a restart
        // genuinely loads something different, and staying silent would be the false negative of
        // the same prompt.
        var restartRequired = held is null
            && (becameHead
                || (!string.Equals(entry.PreviousDirectory, displaced?.PreviousDirectory,
                        StringComparison.OrdinalIgnoreCase)
                    && HeadDoesNotLoadHere(entry)));

        bool HeadDoesNotLoadHere(ModuleActivationEntry head)
        {
            if (ModuleActivationSidecar.ReadUnloadable(baseDirectory, name) is { } measured
                && string.Equals(measured.Generation, head.Directory, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(measured.FrameworkMvid))
                return true;
            var headDll = ModuleActivationBoot.LandedDllPath(baseDirectory, head);
            return !File.Exists(headDll) || !ModulePlatformLink.Check(headDll, surface).MayLoad;
        }

        if (restartRequired)
            ModuleActivationSidecar.SetPendingRestart(baseDirectory, true);

        if (shelfOnly is not null)
            logger?.LogInformation(
                "Module '{Name}' SHELVED into modules/{Generation}/ ({Count} assemblies) but it is "
                + "NOT the head: {Reason}{HeldNote} This registry keeps {Head} as the head; the "
                + "head's fallback is now {Fallback}",
                name, generation, assemblies.Count, shelfOnly,
                held is null ? string.Empty : $" (it is also unloadable here: {held}).",
                entry.Directory, entry.PreviousDirectory ?? "(none)");
        else if (held is null)
            logger?.LogInformation(
                "Module '{Name}' LANDED into modules/{Generation}/ ({Count} assemblies, declared "
                + "floor {MinMeshVersion} — advisory, platform {Running}; built against framework "
                + "MVID {FrameworkMvid} — diagnostic; previous generation {Previous} kept as the "
                + "fallback) — activation recorded, RESTART REQUIRED to load it",
                name, generation, assemblies.Count, minMeshVersion ?? "(none)",
                ModulePlatformFloor.RunningVersion ?? "(unknown)", frameworkMvid ?? "(unrecorded)",
                entry.PreviousDirectory ?? "(none)");
        else
            logger?.LogInformation(
                "Module '{Name}' SHELVED into modules/{Generation}/ ({Count} assemblies) but HELD "
                + "from local activation: {Reason}. It SERVES to consumers from here; this "
                + "process's boot runs the previous generation {Previous} until a platform update "
                + "carries the types it links against, and that same boot then loads it",
                name, generation, assemblies.Count, held, entry.PreviousDirectory ?? "(none)");

        return new ModuleLandingOutcome(Held: held is not null, HoldReason: held)
        {
            ShelfOnlyReason = shelfOnly,
            RetainedAsFallback = retainedAsFallback,
            HeadVersion = entry.Version,
            RestartRequired = restartRequired,
        };
    }

    /// <summary>
    /// The CONTENT ADDRESS of one landing (#3656): 16 lowercase hex characters of SHA-256 over
    /// every file the landing would write — each file's module-relative path, its byte length and
    /// its bytes, ordinal-sorted by path, so the answer depends on the content alone and never on
    /// the order the caller happened to hand the files over in.
    ///
    /// <para>This is what makes two replicas landing one bundle land ONE generation. The
    /// generation leaf was a random id until #3656, and two replicas that both decided to land the
    /// same module — the normal shape of a reconcile wave, since every replica reconciles the same
    /// feed — therefore wrote identical bytes under two names, derived two different module sets
    /// from the activation record and proposed both at the same sequence. See the block comment in
    /// <c>LandCore</c> for the production measurement.</para>
    ///
    /// <para>Sixteen characters (64 bits), deliberately not the eight the random leaf used: a
    /// content address that COLLIDES would make a landing adopt somebody else's bytes, where a
    /// random one merely failed the rename. It also means no legacy <c>name@&lt;8 hex&gt;</c>
    /// directory can ever be mistaken for a content-addressed one — the lengths differ.</para>
    /// </summary>
    /// <param name="assemblies">The assemblies the landing writes, as file name → bytes.</param>
    /// <param name="staticAssets">The static web assets it writes, as module-relative path →
    /// bytes; null or empty when the module ships none.</param>
    /// <param name="nativeAssets">🚨 The RID-specific native payloads it writes (#4126), same
    /// shape. They are part of the address for the same reason the assets are: two bundles that
    /// differ ONLY in their natives are two different landings, and hashing over the assemblies
    /// alone would resolve both to one generation directory — the second adopting the first's
    /// engine (or none) while its activation entry claims its own.</param>
    internal static string GenerationIdOf(
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? nativeAssets = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in (assemblies ?? [])
                     .Select(a => (Path: a.FileName.Replace('\\', '/'), a.Bytes))
                     .Concat((staticAssets ?? [])
                         .Select(a => (Path: a.RelativePath.Replace('\\', '/'), a.Bytes)))
                     .Concat((nativeAssets ?? [])
                         .Select(a => (Path: a.RelativePath.Replace('\\', '/'), a.Bytes)))
                     .OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            // The length goes in as well as the bytes, so no concatenation of two files can hash
            // like a different split of the same stream.
            hash.AppendData(Encoding.UTF8.GetBytes($"{path}\n{bytes?.Length ?? 0}\n"));
            if (bytes is { Length: > 0 })
                hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Takes over a generation directory that was ALREADY on the volume when this landing resolved
    /// its content address — the concurrent-replica case (#3656). The bytes are identical by
    /// construction, so there is nothing to write; what the landing must do is put the directory
    /// back inside the #2303 grace window, because a directory nothing referenced a moment ago is
    /// exactly the directory a GC pass on another replica may be about to reclaim — and the
    /// activation entry written immediately after this is about to reference it.
    /// </summary>
    private void AdoptLandedGeneration(string target, string name, string generation)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Surfaced, never swallowed: the landing itself is complete and correct — the entry
            // below references bytes that are on disk. What is lost is the grace window, so a GC
            // pass that had already read this directory as unreferenced could still reclaim it,
            // and the next reconcile would re-land it. Loud enough to see it happen twice.
            logger?.LogWarning(ex,
                "Module '{Name}': generation {Generation} was already landed by another replica, "
                + "but its timestamp could not be refreshed ({Cause}) — a concurrent GC pass may "
                + "still reclaim it as unreferenced, and the next reconcile re-lands it.",
                name, generation, ex.Message);
        }
    }

    /// <summary>
    /// 🚨 A content-addressed generation's NAME asserts its content, so a directory that carries
    /// the name but lacks a file (a lost entry DLL, a partial volume restore) is an INCOMPLETE
    /// store entry, not a different one. Adopting it as-is recorded a broken generation as the
    /// head, and a re-publish of the very same bytes — the one upload that could heal it —
    /// changed nothing (#4031 review). The staged copy is byte-identical by construction, so every
    /// file the target lacks is moved in from it. A file the target HAS is never touched: a
    /// running pod may hold it open, which is why landings write generations instead of swapping.
    /// </summary>
    private void RestoreMissingFiles(string staging, string target, string name, string generation)
    {
        var restored = ImmutableList<string>.Empty;
        foreach (var staged in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                     .ToImmutableArray())
        {
            var relative = Path.GetRelativePath(staging, staged);
            var destination = Path.Combine(target, relative);
            if (File.Exists(destination))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Move(staged, destination);
                restored = restored.Add(relative);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another replica restored the same file first — identical bytes by construction.
            }
        }
        if (!restored.IsEmpty)
            logger?.LogWarning(
                "Module '{Name}': generation {Generation} was on the volume but INCOMPLETE — restored "
                + "{Count} missing file(s) from this landing's identical bytes: {Files}",
                name, generation, restored.Count, string.Join(", ", restored));
    }

    // Internal for the #4427 review pins (InternalsVisibleTo): an uninstall on a second REPLICA
    // runs inside the first one's projection window, on the calling thread.
    internal void RemoveCore(string name)
    {
        ValidateFileName(name, "module name");

        var list = ModuleActivationSidecar.ReadFor(baseDirectory,
            msg => logger?.LogError("{Message}", msg), platform);
        var existing = list.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            throw new InvalidOperationException(
                $"Module '{name}' was not landed by the store lane (no activation entry) — "
                + "publish-laid-out module folders are managed by the deployment, not uninstall.");

        // BOTH generation pointers are CLEARED on uninstall — a disabled entry must not keep its
        // directory (or its fallback's, #3649) 'referenced', or the GC pass could never reclaim
        // them. Written to THIS module's own file, never through the shared index (#2090): an
        // uninstall racing another module's landing used to drop whichever entry lost.
        //
        // 🚨 #4026, Copilot's review of #4427 — the uninstall is an EVENT first: a tombstone in the
        // module's record directory, immutable and ordered by its own arrival against every landing
        // record. A current image reads "uninstalled" whenever the newest tombstone is newer than
        // every landing, WHATEVER the per-module file says — so a landing on another replica that
        // derived "installed" before this tombstone and writes its projection after it cannot bring
        // the module back. No record is deleted: the landings before the tombstone simply stop
        // counting, which also makes the next landing a first landing (the documented "uninstall,
        // then publish the older build" rollback), and retention retires them once they change
        // nothing. Deleting records instead would be an order nothing can rely on — a landing
        // writing its record while they are deleted.
        var tombstone = ModuleActivationSidecar.WriteUninstall(baseDirectory, name,
            ModuleActivationSidecar.LatestEventName(
                ModuleActivationSidecar.ReadModuleHead(baseDirectory, name, msg => logger?.LogError("{Message}", msg), platform)));
        // The disabled per-module file is what an image that predates the records reads. It is a
        // projection of the tombstone, marked as one, so it decides nothing for a current image.
        beforeProjecting?.Invoke(name);
        ModuleActivationSidecar.WriteEntry(baseDirectory,
            existing with
            {
                Enabled = false,
                Directory = null,
                PreviousDirectory = null,
                PreviousVersion = null,
                PreviousFrameworkMvid = null,
                PreviousSourceCommit = null,
                ProjectionOf = tombstone,
            });
        ModuleActivationSidecar.SetPendingRestart(baseDirectory, true);
        // An uninstalled module has no head to have measured (#3650); a marker left behind would
        // be inert (its generation is gone) but is one more thing to explain.
        ModuleActivationSidecar.ClearUnloadable(baseDirectory, name);
        ModuleActivationSidecar.ClearRefused(baseDirectory, name);

        // Best-effort immediate delete: on a shared volume the files of a LOADED module refuse
        // deletion (SMB keeps them open) — that is fine, the cleared pointers above make the
        // next GC pass (ModuleGenerationsGcHostedService, after a pod's ApplicationStarted)
        // reclaim the generations once no pod holds them.
        var targets = new List<string> { Path.Combine(baseDirectory, "modules", name) };
        if (!string.IsNullOrWhiteSpace(existing.Directory))
            targets.Add(Path.Combine(baseDirectory, "modules", existing.Directory!));
        if (!string.IsNullOrWhiteSpace(existing.PreviousDirectory))
            targets.Add(Path.Combine(baseDirectory, "modules", existing.PreviousDirectory!));
        foreach (var target in targets.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(
                    "Uninstall of '{Name}': {Dir} is in use, the next GC pass reclaims it ({Reason})",
                    name, Path.GetFileName(target), e.Message);
            }
        }

        logger?.LogInformation(
            "Module '{Name}' UNINSTALLED: activation disabled — takes effect at the next restart",
            name);
    }

    /// <summary>
    /// The surface a landing module is measured against (#3538): the application closure, this
    /// process's loaded assemblies, and the ACTIVE generation of every module this deployment has
    /// landed.
    ///
    /// <para>🚨 <b><c>AppContext.BaseDirectory</c>, never <c>baseDirectory</c>, for the platform
    /// half.</b> The platform is the APP closure; this service's root is the (possibly separate,
    /// possibly read-write) volume the <c>modules/</c> tree lives on. Naming the wrong one would
    /// measure a module against the directory it is being written into.</para>
    ///
    /// <para>🚨 <b>The landed modules belong in it, and it is REBUILT per landing.</b> A wave lands
    /// its modules ONE AT A TIME, and a module may legitimately reference a SIBLING module that
    /// landed thirty seconds ago and that this process has not loaded (restart-as-activation). A
    /// surface that knew only <c>/app</c> — or one captured at the wave's first landing — would
    /// not carry that sibling, and the platform-prefix rule would refuse the module for "no such
    /// platform assembly": a false refusal, and exactly the confidently-wrong verdict this gate
    /// exists to replace. The cost is a sidecar read plus the metadata of the assemblies THIS
    /// module references, on the cap-1 IO pool, off every render path.</para>
    ///
    /// <para>The ACTIVE generation specifically, through the one resolution rule
    /// (<see cref="ModuleDirectoryFor"/>) — not every directory under <c>modules/</c>. Superseded
    /// generations are still on the volume until the GC reclaims them, and an older one can
    /// legitimately lack a type its successor has; measuring against whichever directory an
    /// unordered listing happened to yield first would make the verdict depend on the filesystem.</para>
    /// </summary>
    private ModulePlatformSurface PlatformSurface(ModuleActivationList activation)
    {
        var landed = activation
            .Entries
            .Where(entry => entry.Enabled && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => ModuleDirectoryFor(baseDirectory, entry.Name, entry))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return platformSurface([AppContext.BaseDirectory, .. landed]);
    }

    private static void ValidateFileName(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException($"Invalid {what}: '{value}'.");
    }

    /// <summary>
    /// Announces that the persisted activation record changed, containing a subscriber's fault
    /// rather than propagating it: a surface that failed to re-render must never turn a landing that
    /// genuinely succeeded into a reported failure — the write being announced has already happened
    /// by the time this runs — and on the teardown path it must never turn a clean dispose into a
    /// throwing one. The fault is NOT swallowed silently: this line is the only evidence a surface
    /// stopped following the signal.
    /// </summary>
    /// <param name="notify">The emission to make — <c>OnNext</c> after a write, <c>OnCompleted</c>
    /// at dispose. Always applied to the SYNCHRONIZED façade; see the field's remarks.</param>
    private void Announce(Action<ISubject<Unit>> notify)
    {
        try
        {
            notify(announce);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception,
                "A subscriber to ActivationChanged faulted; the module landing itself succeeded.");
        }
    }

    private void AnnounceActivationChanged() => Announce(subject => subject.OnNext(Unit.Default));

    /// <summary>The <see cref="ModuleSetProposed"/> emission, contained exactly like
    /// <see cref="Announce"/>: a faulting subscriber never turns a proposal that landed into a
    /// reported failure.</summary>
    private void AnnounceModuleSetProposed(ModuleSet proposed)
    {
        try
        {
            announceProposed.OnNext(proposed);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception,
                "A subscriber to ModuleSetProposed faulted; the module set was proposed regardless.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Announce(subject => subject.OnCompleted());
        try
        {
            announceProposed.OnCompleted();
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "A subscriber to ModuleSetProposed faulted on completion.");
        }
        activationChanged.Dispose();
        moduleSetProposed.Dispose();
        pool.Dispose();
    }
}

/// <summary>
/// What a landing did — the answer the publish endpoint relays, so a publisher can tell
/// "shelved, will serve" apart from "activated here" (2026-08-22).
/// </summary>
/// <param name="Held">True when the bytes landed but this process's own activation is HELD —
/// the link probe measured the module as unloadable against the running platform (a type it
/// links against is missing, or its metadata could not be read), so boot parks the entry until a
/// platform update carries what it needs. False = the ordinary landing: loads at the next
/// restart. 🚨 Since #3648 a declared <c>minMeshVersion</c> above the running platform does NOT
/// hold — it is recorded and logged as an advisory, and bytes that link land unheld.</param>
/// <param name="HoldReason">Why the activation is held, naming the missing type
/// (<see cref="MeshWeaver.Mesh.ModuleLinkVerdict.Report"/>'s text), or null when not held.</param>
public sealed record ModuleLandingOutcome(bool Held, string? HoldReason)
{
    /// <summary>
    /// Why this upload did NOT become the module's head generation (#3996): its version ranks
    /// strictly below the version this registry already holds as the head, so its bytes land on
    /// the volume while the head — what the registry SERVES, and what its own next restart loads —
    /// stays where it is. The bytes are recorded as the entry's fallback generation (and so kept
    /// and served at their own version) ONLY when <see cref="RetainedAsFallback"/> is true;
    /// otherwise the modules GC reclaims them. Null for the ordinary case where the head moved.
    ///
    /// <para>An INIT property rather than a third primary-constructor parameter on purpose: adding
    /// one replaces this public record's constructor and <c>Deconstruct</c> signatures, which is a
    /// binary break for every repo in the fleet that holds an outcome (the same rule
    /// <c>BundleReader.AssemblyRef.SourceFingerprint</c> states).</para>
    /// </summary>
    public string? ShelfOnlyReason { get; init; }

    /// <summary>True when this upload was shelved WITHOUT becoming the head — see
    /// <see cref="ShelfOnlyReason"/>. Orthogonal to <see cref="Held"/>: an older upload can also be
    /// unloadable here, and each says a different thing to the publisher.</summary>
    public bool ShelfOnly => ShelfOnlyReason is not null;

    /// <summary>For a <see cref="ShelfOnly"/> upload: whether its generation was KEPT, as the head's
    /// fallback (<see cref="ModuleActivationEntry.PreviousDirectory"/>), so it stays on the volume,
    /// in the bundle index and downloadable at its own version. False means the fallback slot kept
    /// a better generation (loadable here first, then the higher version) and the modules GC
    /// reclaims this one — the entry has ONE fallback slot, so a registry holds at most two
    /// generations of a module. Always false when the head moved.</summary>
    public bool RetainedAsFallback { get; init; }

    /// <summary>The version at the activation head AFTER this landing: the incoming version when
    /// the head moved, the retained newer head's version for a <see cref="ShelfOnly"/> upload.</summary>
    public string? HeadVersion { get; init; }

    /// <summary>Whether this landing raised the deployment's pending-restart flag, i.e. whether this
    /// instance loads something DIFFERENT at its next restart because of it. True for an ordinary
    /// unheld landing; false for a held one (boot parks it again) and for a shelf-only one — except
    /// when the shelf-only upload moved the fallback while the head does not load here, because boot
    /// then runs the fallback it names.</summary>
    public bool RestartRequired { get; init; }
}
