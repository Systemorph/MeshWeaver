using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
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
/// <para><b>The platform-floor gate holds at placement.</b> Landing verifies the module's
/// declared <c>minMeshVersion</c> through <see cref="ModulePlatformFloor.DeclineReason(string?)"/>
/// — the ONE notion of the module platform gate, shared with the serve and fetch sides. An
/// unsatisfied floor REFUSES the landing (the observable errors, naming both versions); declined
/// bytes never reach disk. The framework MVID the bundle was built against is recorded and logged
/// as DIAGNOSTIC metadata only — modules bind by simple name, and their contract is API
/// compatibility, not build identity (that strict gate belongs to the NodeType bake lane).</para>
///
/// <para><b>…except on the SHELF lane, where an unsatisfied floor HOLDS instead of refusing.</b>
/// <see cref="ShelveModule"/> is the PUBLISH path's entry (the registry stocking its warehouse):
/// a warehouse may carry modules for platforms newer than itself, so above-floor bytes land and
/// their activation entry is recorded — but nothing here loads them: boot re-applies the SAME
/// floor gate per entry (<see cref="ModuleActivationBoot.ComputeEffectiveModuleEntries"/>) and
/// skips a held entry loudly, until a platform update satisfies the floor and the very same boot
/// check activates it. There is deliberately NO persisted "held" flag — held-ness is DERIVED from
/// the recorded floor against the running platform at each decision point, so it can never go
/// stale when the platform moves (the one-notion rule again). <see cref="LandModule"/> — the
/// direct-adopt funnel — keeps refusing: an instance must never hold bytes its own next boot
/// would try to load into an unsatisfied platform… which for the adopt path is the point of the
/// install, so a hold there would be a package whose binary half silently never arrives.</para>
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
/// contend for one file's SMB lease.</para>
///
/// <para><b>…except boot-time GC against a CONCURRENT landing (#2303).</b> A landing's two writes
/// (move the bytes, then <see cref="ModuleActivationSidecar.WriteEntry"/>) are not atomic across
/// replicas, so another replica's <see cref="CollectGarbage"/> can observe the new generation
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
    /// <see cref="CollectGarbage"/> treats it as truly orphaned rather than the first half of a
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
    /// it — <see cref="ModuleActivationStatus.Unresolvable"/>'s loud report, or a boot that skips
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
    /// Boot-time garbage collection over <c>modules/</c>: deletes generation directories
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>) no activation entry references, leftover
    /// <c>.staging-*</c>, and the retired <c>.pending-*</c> folders of the abandoned deferred-swap
    /// scheme — but only once they are older than <paramref name="minAge"/>
    /// (<see cref="DefaultGarbageMinAge"/>): see that constant for why an UNREFERENCED directory is
    /// not proof of an orphan (#2303). Skip-on-locked: a directory some still-running pod holds
    /// open simply survives to the next boot. Never touches legacy <c>&lt;name&gt;/</c> folders —
    /// entries without a generation still resolve there.
    /// </summary>
    /// <param name="baseDirectory">The deployment root whose <c>modules/</c> folder is swept.</param>
    /// <param name="logger">Diagnostics — every removal and every age-deferred skip is logged.</param>
    /// <param name="minAge">The grace period below which an unreferenced directory is left alone.
    /// Defaults to <see cref="DefaultGarbageMinAge"/>; a test seam otherwise.</param>
    /// <param name="nowUtc">The reference "now" the age check compares against. Defaults to
    /// <see cref="DateTime.UtcNow"/>; a test seam so the race and its fix are provable without a
    /// real sleep.</param>
    /// <returns>How many directories were removed.</returns>
    public static int CollectGarbage(
        string baseDirectory, ILogger? logger = null, TimeSpan? minAge = null, DateTime? nowUtc = null)
    {
        var modulesRoot = Path.Combine(baseDirectory, "modules");
        if (!Directory.Exists(modulesRoot))
            return 0;
        var activation = ModuleActivationSidecar.Read(baseDirectory,
            msg => logger?.LogError("{Message}", msg));
        var referenced = activation.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Directory))
            .Select(e => e.Directory!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = (nowUtc ?? DateTime.UtcNow) - (minAge ?? DefaultGarbageMinAge);
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(modulesRoot))
        {
            var leaf = Path.GetFileName(dir);
            var collectable =
                leaf.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase)
                || leaf.StartsWith(".pending-", StringComparison.OrdinalIgnoreCase)
                || (leaf.Contains('@') && !referenced.Contains(leaf));
            if (!collectable)
                continue;
            // 🚨 #2303: an unreferenced directory younger than the grace window may simply be a
            // landing this replica has not seen the ACTIVATION ENTRY for yet — see
            // DefaultGarbageMinAge. Left for a later pass, which re-reads the sidecar and either
            // finds the entry now present (survives, correctly) or is still unreferenced and past
            // the window (a genuine orphan, collected then).
            if (Directory.GetLastWriteTimeUtc(dir) > cutoff)
            {
                logger?.LogDebug(
                    "Modules GC: {Dir} is unreferenced but younger than the {MinAge} grace period "
                    + "— left in case a concurrent landing's activation entry has not landed yet.",
                    leaf, minAge ?? DefaultGarbageMinAge);
                continue;
            }
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
                logger?.LogInformation("Modules GC: removed {Dir}", leaf);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open by a still-running pod — the next boot collects it.
                logger?.LogDebug("Modules GC: {Dir} is in use, skipped ({Reason})", leaf, e.Message);
            }
        }
        return removed;
    }

    // Cap 1 deliberately: landing writes files and the cap IS the in-process serialization — no
    // lock, no semaphore outside the sealed IoPool primitive. Landing is rare and short; 1 costs
    // nothing. 🚨 It is NOT what makes the activation record safe: this pool bounds ONE process,
    // and /data is shared by every replica. Cross-process safety comes from the record's SHAPE —
    // one file per module (ModuleActivationSidecar), so concurrent writers never share a path.
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
    }

    /// <summary>The deployment root the <c>modules/</c> tree lives under — exposed so the serving
    /// side (<see cref="ModuleBundleSource"/> callers) reads the SAME tree this service writes,
    /// tests included.</summary>
    public string BaseDirectory => baseDirectory;

    /// <summary>
    /// Lands a module: verifies the declared platform floor, writes the assemblies atomically into
    /// <c>modules/&lt;name&gt;/</c>, and appends/updates the activation entry in the
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
    /// <param name="minMeshVersion">The module's declared platform FLOOR — the gate: an
    /// unsatisfied floor (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>) refuses the
    /// landing. Null = no constraint declared.</param>
    public IObservable<Unit> LandModule(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid = null,
        string? packagePath = null,
        string? version = null,
        string? minMeshVersion = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets = null)
        => pool.InvokeBlocking(_ =>
        {
            LandCore(name, assemblies, frameworkMvid, packagePath, version, minMeshVersion,
                staticAssets, holdAboveFloor: false);
            return Unit.Default;
        })
        .Do(_ => AnnounceActivationChanged());

    /// <summary>
    /// Lands a module onto the REGISTRY SHELF (2026-08-22) — the publish path's entry, identical to
    /// <see cref="LandModule"/> in every rule but one: an UNSATISFIED platform floor lands the
    /// bytes and records the activation entry as HELD instead of refusing.
    ///
    /// <para><b>Why the two paths must differ.</b> The landing serves two roles: adopting a module
    /// for THIS runtime (the install funnel — the floor refusal is exactly right there, declined
    /// bytes must never reach a disk the next boot loads from), and STOCKING the registry's shelf
    /// (the publish endpoint). Applying the adopt rule to the shelf produced a three-way deadlock,
    /// measured in production 2026-08-22: modules extracted from the platform image declared
    /// <c>minMeshVersion: 3.0.0-rc7</c>, the registry ran rc6 and 409'd every upload — while its
    /// own <c>Modules:Required</c> gate held the rc7 rollout for exactly those absent modules.
    /// The image doesn't ship them → only the registry can deliver them → the registry refuses to
    /// even CARRY them until it updates → it can't update without them.</para>
    ///
    /// <para><b>What "held" means mechanically — no new state, the existing gates ARE the hold.</b>
    /// The bytes go into a generation directory and the entry is recorded (enabled, floor
    /// included) exactly as for an active landing, so the serve side lists and serves them to
    /// consumers, whose own fetch/land chain applies the floor against THEIR platform. This
    /// process's boot does NOT load them: the per-entry floor gate in
    /// <see cref="ModuleActivationBoot.ComputeEffectiveModuleEntries"/> skips the entry with a
    /// loud line naming both versions — and flips it to loaded, on that same normal path, at the
    /// first boot whose platform satisfies the floor (a platform update IS a restart, so no
    /// separate reconcile is needed). The one deliberate difference in the record:
    /// <c>PendingRestart</c> is NOT raised for a held landing — a restart cannot activate it, and
    /// a "restart required" no restart can clear is a false prompt
    /// (<see cref="ModuleActivationStatus.NotYetLoaded"/> excludes held entries for the same
    /// reason).</para>
    ///
    /// <para>Every other refusal is unchanged — in particular the app-closure same-identity
    /// trap-door still refuses even in shelf mode, because a held module DOES load eventually and
    /// would shadow the platform binary then. Cold; the outcome says whether the landing was held
    /// and why, so the publish endpoint can tell its caller "shelved, will serve" apart from
    /// "activated here".</para>
    /// </summary>
    public IObservable<ModuleLandingOutcome> ShelveModule(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid = null,
        string? packagePath = null,
        string? version = null,
        string? minMeshVersion = null,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets = null)
        => pool.InvokeBlocking(_ =>
            LandCore(name, assemblies, frameworkMvid, packagePath, version, minMeshVersion,
                staticAssets, holdAboveFloor: true))
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
    /// Reads the current activation list on this service's IO pool — the runtime counterpart of the
    /// boot-time <see cref="ModuleActivationSidecar.Read"/>, serialized behind the same cap-1 pool
    /// as the writes so a read never observes a landing halfway through its read-modify-write.
    /// </summary>
    public IObservable<ModuleActivationList> GetActivation()
        => pool.InvokeBlocking(_ => ModuleActivationSidecar.Read(baseDirectory,
            msg => logger?.LogError("{Message}", msg)));

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

    private ModuleLandingOutcome LandCore(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid,
        string? packagePath,
        string? version,
        string? minMeshVersion,
        IReadOnlyList<(string RelativePath, byte[] Bytes)>? staticAssets,
        bool holdAboveFloor)
    {
        foreach (var (relativePath, _) in staticAssets ?? [])
            ValidateAssetPath(relativePath, name);
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

        // 🚨 THE PLATFORM-FLOOR GATE, at placement — the same pure function the fetch and boot
        // sides gate on (ModulePlatformFloor), so there is never a second notion of the module
        // platform requirement. Deliberately NOT MVID equality: modules bind by simple name and
        // their contract is API compatibility — the strict MVID gate is bake semantics and stays
        // with the NodeType lane. On the ADOPT path, declining an unsatisfied floor is always
        // safe; landing on faith is not: the missing API would surface only at the next boot, as
        // a MissingMethodException with nothing connecting it to the install that caused it. On
        // the SHELF path (holdAboveFloor — the publish endpoint, see ShelveModule) the same
        // verdict HOLDS instead of refusing: the bytes land for CONSUMERS, whose own gates apply
        // this very function against their platforms, while this process's boot keeps applying it
        // per entry and so never loads what it records here.
        var held = ModulePlatformFloor.DeclineReason(minMeshVersion);
        if (held is not null && !holdAboveFloor)
        {
            logger?.LogWarning("Module '{Name}' REFUSED at landing: {Reason}", name, held);
            throw new InvalidOperationException($"Module '{name}' refused: {held}");
        }

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
        // garbage-collected at boot (CollectGarbage), skip-on-locked, once no entry references
        // them.
        var modulesRoot = Path.Combine(baseDirectory, "modules");
        var generation = $"{name}@{Guid.NewGuid():N}"[..(name.Length + 9)];
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
            Directory.Move(staging, target);
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

        var entry = new ModuleActivationEntry
        {
            Name = name,
            Source = ModuleActivationSources.Store,
            PackagePath = packagePath,
            FrameworkMvid = frameworkMvid,
            Version = version,
            MinMeshVersion = minMeshVersion,
            Enabled = true,
            Directory = generation,
        };
        // 🚨 THIS MODULE'S OWN FILE, and nothing else (#2090). The landing used to read the whole
        // shared activation index, append to it and rename the result over the live file — a
        // read-modify-write of state every replica shares on the RWX /data volume. Two concurrent
        // landings of DIFFERENT modules therefore raced: the later write silently dropped the
        // earlier module's entry, and the rename itself contended for the SMB lease on the one hot
        // file ('Access to the path …/activation.json is denied' → HTTP 409). Writing only
        // activation.d/<Name>.json removes the shared cell instead of guarding it — different
        // modules no longer share a path at all.
        ModuleActivationSidecar.WriteEntry(baseDirectory, entry);
        // A HELD landing does not raise the restart signal: a restart cannot activate it (the boot
        // gate skips the entry until the platform satisfies its floor), so "restart required"
        // would be a prompt no restart can clear. The platform update that DOES satisfy the floor
        // is itself a restart, which activates the entry with no flag.
        if (held is null)
            ModuleActivationSidecar.SetPendingRestart(baseDirectory, true);

        if (held is null)
            logger?.LogInformation(
                "Module '{Name}' LANDED into modules/{Generation}/ ({Count} assemblies, floor "
                + "{MinMeshVersion}, platform {Running}; built against framework MVID "
                + "{FrameworkMvid} — diagnostic) — activation recorded, RESTART REQUIRED to load it",
                name, generation, assemblies.Count, minMeshVersion ?? "(none)",
                ModulePlatformFloor.RunningVersion ?? "(unknown)", frameworkMvid ?? "(unrecorded)");
        else
            logger?.LogInformation(
                "Module '{Name}' SHELVED into modules/{Generation}/ ({Count} assemblies) but HELD "
                + "from local activation: {Reason}. It SERVES to consumers from here; this "
                + "process's boot skips it until a platform update satisfies the floor, and that "
                + "same boot then loads it",
                name, generation, assemblies.Count, held);

        return new ModuleLandingOutcome(Held: held is not null, HoldReason: held);
    }

    private void RemoveCore(string name)
    {
        ValidateFileName(name, "module name");

        var list = ModuleActivationSidecar.Read(baseDirectory,
            msg => logger?.LogError("{Message}", msg));
        var existing = list.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            throw new InvalidOperationException(
                $"Module '{name}' was not landed by the store lane (no activation entry) — "
                + "publish-laid-out module folders are managed by the deployment, not uninstall.");

        // The generation pointer is CLEARED on uninstall — a disabled entry must not keep its
        // directory 'referenced', or boot GC could never reclaim it. Written to THIS module's own
        // file, never through the shared index (#2090): an uninstall racing another module's
        // landing used to drop whichever entry lost.
        ModuleActivationSidecar.WriteEntry(baseDirectory,
            existing with { Enabled = false, Directory = null });
        ModuleActivationSidecar.SetPendingRestart(baseDirectory, true);

        // Best-effort immediate delete: on a shared volume the files of a LOADED module refuse
        // deletion (SMB keeps them open) — that is fine, the cleared pointer above makes the
        // next boot's CollectGarbage reclaim the generation once no pod holds it.
        var targets = new List<string> { Path.Combine(baseDirectory, "modules", name) };
        if (!string.IsNullOrWhiteSpace(existing.Directory))
            targets.Add(Path.Combine(baseDirectory, "modules", existing.Directory!));
        foreach (var target in targets.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger?.LogDebug(
                    "Uninstall of '{Name}': {Dir} is in use, boot GC reclaims it ({Reason})",
                    name, Path.GetFileName(target), e.Message);
            }
        }

        logger?.LogInformation(
            "Module '{Name}' UNINSTALLED: activation disabled — takes effect at the next restart",
            name);
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

    /// <inheritdoc />
    public void Dispose()
    {
        Announce(subject => subject.OnCompleted());
        activationChanged.Dispose();
        pool.Dispose();
    }
}

/// <summary>
/// What a landing did — the answer the publish endpoint relays, so a publisher can tell
/// "shelved, will serve" apart from "activated here" (2026-08-22).
/// </summary>
/// <param name="Held">True when the bytes landed but this process's own activation is HELD —
/// the module's declared floor exceeds the running platform, so boot skips the entry until a
/// platform update satisfies it. False = the ordinary landing: loads at the next restart.</param>
/// <param name="HoldReason">Why the activation is held, naming both versions
/// (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>'s text), or null when not held.</param>
public sealed record ModuleLandingOutcome(bool Held, string? HoldReason);
