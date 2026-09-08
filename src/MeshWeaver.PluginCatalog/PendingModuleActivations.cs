using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What this process can say about landed-but-unloaded modules — either an ANSWER, or an explicit
/// admission that it could not determine one.
///
/// <para>🚨 The two are not the same and must never render the same. "Nothing pending" is evidence;
/// "I could not read the activation sidecar" is the absence of evidence, and a surface that shows
/// the second as the first is exactly the gate that cannot run but looks like a gate that
/// passed.</para>
/// </summary>
/// <param name="Pending">The modules landed on the volume but not loaded in this process — a
/// restart activates them.</param>
/// <param name="UndeterminedReason">Why the answer is unknown, or <c>null</c> when it is known.
/// When set, <paramref name="Pending"/> is empty and means nothing.</param>
/// <param name="Unresolvable">🚨 The modules the activation record says are ON but whose landed
/// assemblies are ABSENT (#2093). A restart will NOT load them — boot skips them — so they are
/// deliberately NOT folded into <paramref name="Pending"/>: a "restart required" no restart can
/// clear is the same lie as a green tick over a gate that never ran, and the remedy is different
/// (re-install, not wait).</param>
public sealed record ModuleActivationReport(
    ImmutableList<PendingModuleActivation> Pending,
    string? UndeterminedReason = null,
    ImmutableList<PendingModuleActivation>? Unresolvable = null)
{
    /// <summary>The activated modules whose bytes are gone. Never null.</summary>
    public ImmutableList<PendingModuleActivation> Unresolvable { get; init; } = Unresolvable ?? [];

    /// <summary>
    /// 🚨 The modules that have LANDED on the volume but are in no proposed module set (#3395) —
    /// a landing wave that has not completed. A restart does NOT activate them, because boot loads
    /// the mesh's set and they are not in it, so they are a THIRD state and never folded into
    /// <see cref="Pending"/>.
    ///
    /// <para>Two causes, one report. While a wave is running this is the wave's own progress and
    /// clears when it proposes, in seconds. A wave that DIED leaves it standing — and that is the
    /// honest failure: the module's bytes are on the volume and it is running on NO replica, said
    /// out loud, instead of the old behaviour where it silently ran on whichever pods happened to
    /// boot after its own landing and not on the others.</para>
    ///
    /// <para>An init-only property rather than a fourth positional parameter: replacing
    /// the constructor signature is what <see cref="MissingMethodException"/>-aborts a host
    /// compiled against the previous platform — the same rule the
    /// <see cref="ModuleActivationStatus"/> overloads follow.</para>
    /// </summary>
    public ImmutableList<PendingModuleActivation> Deferred { get; init; } = [];

    /// <summary>
    /// 🚨 The modules this process REFUSED TO LOAD because their bytes are linked against a
    /// platform it is not running (#3538) — a FOURTH state, and again not folded into
    /// <see cref="Pending"/> for the same reason as the other two: a restart re-runs the same
    /// measurement and reaches the same verdict, so "restart required" would be a prompt no
    /// restart can clear.
    ///
    /// <para>The remedy is different again, and it is the only one of the four that is not the
    /// operator's: this module becomes loadable when the PLATFORM updates, and the platform update
    /// is itself a restart. Until then the module contributes nothing and says so — which is the
    /// whole point, because the alternative that shipped was contributing a
    /// <c>TypeLoadException</c> to every render that touched it.</para>
    ///
    /// <para>An init-only property rather than a positional parameter — replacing a public
    /// record's constructor signature is what <see cref="MissingMethodException"/>-aborts a host
    /// compiled against the previous platform.</para>
    /// </summary>
    public ImmutableList<PendingModuleActivation> Quarantined { get; init; } = [];

    /// <summary>True when the state is KNOWN and a module was refused as unloadable against this
    /// platform build.</summary>
    public bool HasQuarantined => !IsUndetermined && !Quarantined.IsEmpty;

    /// <summary>
    /// 🚨 The declared-floor ADVISORIES (#3648): every enabled module the mesh's set activates whose
    /// recorded <c>minMeshVersion</c> ranks above the running platform, loaded or not. NOT a fifth
    /// state — none of these is held, skipped or refused on the string; each is pending,
    /// quarantined or running exactly as the other lists say, and this list merely adds what the
    /// module CLAIMS, so a status row can read "declares platform ≥ X; running Y". Maintainer
    /// directive 2026-09-07: whether a module loads is measured (the link probe →
    /// <see cref="Quarantined"/>), never declared. Init-only, for the same binary-compatibility
    /// reason as the properties above.
    /// </summary>
    public ImmutableList<ModuleFloorAdvisory> FloorAdvisories { get; init; } = [];

    /// <summary>True when the state is KNOWN and at least one module declares a floor above the
    /// running platform.</summary>
    public bool HasFloorAdvisories => !IsUndetermined && !FloorAdvisories.IsEmpty;

    /// <summary>
    /// 🚨 The modules that RUN THEIR PREVIOUS GENERATION here because the one the mesh's set
    /// activates does not load on this platform (#3649) — a FIFTH state, and the first one that
    /// is not a fault: the module is present and working, one version behind. Not folded into
    /// <see cref="Pending"/> (a restart re-runs the same measurement and falls back again, so
    /// "restart required" would be a prompt no restart clears) and not into
    /// <see cref="Quarantined"/> (that one contributes nothing; this one contributes everything
    /// its previous version did). Each row names both generations and why. Init-only, for the
    /// same binary-compatibility reason as the properties above.
    /// </summary>
    public ImmutableList<ModuleFallback> Fallbacks { get; init; } = [];

    /// <summary>True when the state is KNOWN and a module runs its previous generation.</summary>
    public bool HasFallbacks => !IsUndetermined && !Fallbacks.IsEmpty;

    /// <summary>
    /// The fallback row for the module the install record at <paramref name="packagePath"/>
    /// landed, or null when that module runs the generation the set activates (or the state is
    /// undetermined). Blank matches nothing — never a wildcard.
    /// </summary>
    public ModuleFallback? FallbackForPackage(string? packagePath) =>
        IsUndetermined || string.IsNullOrWhiteSpace(packagePath)
            ? null
            : Fallbacks.FirstOrDefault(f => string.Equals(
                f.PackagePath?.Trim('/'), packagePath.Trim('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The declared-floor advisory for the module the install record at
    /// <paramref name="packagePath"/> landed, or null when it declares none above the running
    /// platform (or the state is undetermined). Blank matches nothing — never a wildcard.
    /// </summary>
    public ModuleFloorAdvisory? FloorAdvisoryForPackage(string? packagePath) =>
        IsUndetermined || string.IsNullOrWhiteSpace(packagePath)
            ? null
            : FloorAdvisories.FirstOrDefault(a => string.Equals(
                a.PackagePath?.Trim('/'), packagePath.Trim('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the install record at <paramref name="packagePath"/> landed a module this process
    /// refused to load (#3538) — the per-PACKAGE question a package card asks so it can say
    /// "built for a newer platform" instead of a bare "installed" or, worse, a "restart required"
    /// that no restart clears.
    /// </summary>
    /// <param name="packagePath">The install record's mesh path. Blank matches nothing.</param>
    public bool IsQuarantinedForPackage(string? packagePath) =>
        !IsUndetermined && ModuleActivationStatus.IsPendingForPackage(Quarantined, packagePath);

    /// <summary>One line naming which module set the mesh is on and whether a replica has booted
    /// onto it yet (<see cref="ModuleSetStore.Describe"/>), or null on a deployment with no set
    /// records. The mesh-level half of the report; <see cref="Pending"/> is the per-pod half.</summary>
    public string? MeshModuleSet { get; init; }

    /// <summary>True when the state is KNOWN and a landing wave has not proposed its set.</summary>
    public bool HasDeferred => !IsUndetermined && !Deferred.IsEmpty;

    /// <summary>True when this process could not establish the activation state at all.</summary>
    public bool IsUndetermined => UndeterminedReason is not null;

    /// <summary>True when the state is KNOWN and something is waiting on a restart.</summary>
    public bool HasPending => !IsUndetermined && !Pending.IsEmpty;

    /// <summary>True when the state is KNOWN and an activated module can never load as it stands.
    /// The FAULT half of the report — a restart does not clear it.</summary>
    public bool HasUnresolvable => !IsUndetermined && !Unresolvable.IsEmpty;

    /// <summary>
    /// Whether the install record at <paramref name="packagePath"/> landed a module that has not
    /// loaded in this process — the per-PACKAGE question a package card asks so it can say "restart
    /// required to finish activating this" instead of a bare "installed" (#1979).
    ///
    /// <para>Lives on the REPORT rather than only on the reader so a surface rendering many cards
    /// answers them all from ONE read of the activation record, instead of re-reading it per card.
    /// <see cref="PendingModuleActivations.IsPendingForPackage"/> is the same rule with the read
    /// attached — one rule, two entry points, so their answers cannot diverge.</para>
    ///
    /// <para>🚨 Returns <c>false</c> when the state is UNDETERMINED, and that is deliberate: this
    /// answers a per-package question whose only honest fallback is "I have nothing to say about
    /// this package". An unreadable activation record is an OPERATOR signal — the health check
    /// reports it, where it is actionable — and putting "restart required" on every package card
    /// because a file would not parse is noise a buyer cannot act on.</para>
    /// </summary>
    /// <param name="packagePath">The install record's mesh path. Blank matches nothing; it is not a
    /// wildcard, so a card with no path to match on cannot inherit another package's restart.</param>
    /// <returns>True when a restart of THIS process would activate a module this package landed.</returns>
    public bool IsPendingForPackage(string? packagePath) =>
        !IsUndetermined && ModuleActivationStatus.IsPendingForPackage(Pending, packagePath);

    /// <summary>The one line every surface renders, so nobody is told two different stories.</summary>
    public string Describe() =>
        UndeterminedReason is { } reason
            ? "module activation state could not be determined — " + reason
            : (HasUnresolvable
                ? ModuleActivationStatus.DescribeUnresolvable(Unresolvable)
                    + (HasPending ? "; " + ModuleActivationStatus.Describe(Pending) : string.Empty)
                : ModuleActivationStatus.Describe(Pending))
              + (HasDeferred ? "; " + DescribeDeferred(Deferred) : string.Empty)
              + (HasQuarantined ? "; " + DescribeQuarantined(Quarantined) : string.Empty)
              + (HasFallbacks ? "; " + DescribeFallbacks(Fallbacks) : string.Empty)
              + (HasFloorAdvisories ? "; " + DescribeFloorAdvisories(FloorAdvisories) : string.Empty);

    /// <summary>
    /// One human-readable line naming the modules that run their PREVIOUS generation (#3649) —
    /// one named row per module, "X runs v1.2.3 (gen A); v1.3.0 (gen B) landed but does not load
    /// here: …" — kept apart from every other line because it asks for nothing of the operator:
    /// the module works, and the newest generation starts running by itself when a build of it
    /// that loads here ships or the platform moves.
    /// </summary>
    /// <param name="fallbacks">The fallback rows.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string DescribeFallbacks(
        IReadOnlyCollection<ModuleFallback> fallbacks, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        if (fallbacks.Count == 0)
            return "no module runs a previous generation";
        var rows = fallbacks
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => $"{f.Name} {f.Reason}")
            .ToArray();
        return $"{rows.Length} module(s) run a PREVIOUS generation because the one the mesh's set "
            + "activates does not load on this platform — present and working, one version "
            + "behind; no restart changes that, a build that loads here does: "
            + string.Join("; ", rows.Take(Math.Max(1, maxNamed)))
            + (rows.Length > maxNamed ? $"; …(+{rows.Length - maxNamed})" : string.Empty);
    }

    /// <summary>
    /// One human-readable line naming the modules that DECLARE a platform above the one running
    /// (#3648) — an advisory, kept apart from every other line because it asks for nothing: the
    /// module loads or not on what the link probe measured, and this only says what its author
    /// claimed.
    /// </summary>
    /// <param name="advisories">The declared-floor advisories.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string DescribeFloorAdvisories(
        IReadOnlyCollection<ModuleFloorAdvisory> advisories, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(advisories);
        if (advisories.Count == 0)
            return "no module declares a platform above the one running";
        var named = advisories
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => $"{a.Name} (≥ {a.DeclaredFloor})")
            .ToArray();
        return $"{named.Length} module(s) declare a platform above the one running "
            + $"({advisories.First().RunningVersion ?? "unknown"}) — advisory only, loadability is "
            + "measured by the link probe, never by the declared floor: "
            + string.Join(", ", named.Take(Math.Max(1, maxNamed)))
            + (named.Length > maxNamed ? $", …(+{named.Length - maxNamed})" : string.Empty);
    }

    /// <summary>
    /// One human-readable line naming the modules this process refused to load because their bytes
    /// need a platform it is not running (#3538) — kept apart from every other line because the
    /// remedy is apart from every other remedy: the PLATFORM has to move.
    /// </summary>
    /// <param name="quarantined">The refused modules.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string DescribeQuarantined(
        IReadOnlyCollection<PendingModuleActivation> quarantined, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(quarantined);
        if (quarantined.Count == 0)
            return "no module was refused against this platform build";
        var names = quarantined
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return $"{names.Length} module(s) were REFUSED against this platform build — their bytes "
            + "are linked against a platform this deployment is not running, so they contribute "
            + "nothing and no restart changes that; the platform update that satisfies them is "
            + "itself the restart that loads them: "
            + string.Join(", ", names.Take(maxNamed))
            + (names.Length > maxNamed ? $" (+{names.Length - maxNamed} more)" : string.Empty);
    }

    /// <summary>
    /// One human-readable line naming the modules a landing wave landed but never proposed
    /// (#3395) — kept separate from both other lines because the remedy differs again: nothing an
    /// operator does to THIS pod helps, the wave has to complete.
    /// </summary>
    /// <param name="deferred">The landed-but-unproposed modules.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string DescribeDeferred(
        IReadOnlyCollection<PendingModuleActivation> deferred, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(deferred);
        if (deferred.Count == 0)
            return "no module is waiting on a landing wave";
        var names = deferred
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return $"{names.Length} module(s) have landed but are in NO proposed module set — the "
            + "landing wave that brought them has not completed, so they are running on no replica "
            + "and a restart will not change that; the next completed wave activates them: "
            + string.Join(", ", names.Take(Math.Max(1, maxNamed)))
            + (names.Length > maxNamed ? $", …(+{names.Length - maxNamed})" : string.Empty);
    }
}

/// <summary>
/// One module's declared-floor ADVISORY (#3648): what its author claimed about the platform it
/// needs, beside what this deployment runs. Never a state — see
/// <see cref="ModuleActivationReport.FloorAdvisories"/>.
/// </summary>
/// <param name="Name">The module's assembly simple name, as the activation entry records it.</param>
/// <param name="PackagePath">The mesh path of the install record that landed it, when recorded.</param>
/// <param name="DeclaredFloor">The recorded <c>minMeshVersion</c>.</param>
/// <param name="RunningVersion">The platform version this process runs, or null when unstamped.</param>
/// <param name="Reason">The sentence naming both versions
/// (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>'s text).</param>
public sealed record ModuleFloorAdvisory(
    string Name, string? PackagePath, string DeclaredFloor, string? RunningVersion, string Reason);

/// <summary>
/// One module that runs its PREVIOUS generation on this replica (#3649): which generation runs,
/// which one landed and does not load here, and why — the row every status surface renders.
/// </summary>
/// <param name="Name">The module's assembly simple name, as the activation entry records it.</param>
/// <param name="PackagePath">The mesh path of the install record that landed it, when recorded.</param>
/// <param name="Version">The package version of the generation that does NOT load here, when recorded.</param>
/// <param name="Generation">The generation directory leaf that does NOT load here.</param>
/// <param name="PreviousVersion">The package version of the generation that runs, when recorded.</param>
/// <param name="PreviousGeneration">The generation directory leaf that runs.</param>
/// <param name="Reason">The row's sentence (<see cref="MeshWeaver.Mesh.FallbackModule.Describe"/>):
/// "runs v1.2.3 (gen A); v1.3.0 (gen B) landed but does not load here: …".</param>
public sealed record ModuleFallback(
    string Name,
    string? PackagePath,
    string? Version,
    string Generation,
    string? PreviousVersion,
    string PreviousGeneration,
    string Reason);

/// <summary>
/// Reads the restart-as-activation state for THIS process: the persisted activation sidecar
/// compared against the assemblies actually loaded here (#1979).
///
/// <para>This is the seam every surface consults — the operator health check, and (through
/// <c>hub.ServiceProvider</c>) a package card that needs to say "restart required to finish
/// activating this" instead of a bare "installed". One reader, so the numbers cannot
/// disagree.</para>
///
/// <para>A pull-on-demand READER: it starts nothing, subscribes to nothing and writes nothing, so
/// an instance that never asks pays nothing. The read is a single small file, which is why it is
/// plain and synchronous — the same reason <see cref="ModuleActivationSidecar"/> is.</para>
/// </summary>
public sealed class PendingModuleActivations(string moduleRoot)
{
    /// <summary>Constructs from configuration, resolving the writable module root once.</summary>
    public PendingModuleActivations(IConfiguration? configuration)
        : this(ModuleRoot.Resolve(configuration)) { }

    /// <summary>The deployment root whose <c>modules/</c> sidecar is read.</summary>
    public string ModuleRootPath { get; } = moduleRoot;

    /// <summary>
    /// The simple names of modules this process REFUSED to load (#3538) — production passes the
    /// registered <see cref="Mesh.IncompatibleModule"/> set, which is what
    /// <c>MeshBuilder.InstallAssemblies</c> recorded for every module its link probe declined and
    /// every module whose registration threw.
    ///
    /// <para>🚨 Without it such a module reads as PENDING — its assembly is genuinely not loaded —
    /// and the surface promises a restart that re-runs the same measurement and refuses again.
    /// An init-only property, not a constructor parameter: replacing the constructor signature is
    /// a binary break for a host compiled against the previous platform.</para>
    /// </summary>
    public IReadOnlySet<string> QuarantinedModules { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The modules this process installed from their PREVIOUS generation (#3649) — production
    /// passes the registered <see cref="Mesh.FallbackModule"/> set, which is what
    /// <c>MeshBuilder.InstallModules</c> recorded for every module whose head generation did not
    /// load here and whose previous one did.
    ///
    /// <para>🚨 Without it such a module reads as PENDING: the generation the set activates is
    /// not the one loaded here, which is the update half of "not loaded" (#3395) — and the
    /// surface would promise a restart that re-runs the same measurement and falls back again.
    /// It is pending only when the set has since moved to a generation OTHER than the one the
    /// loader refused, which a restart genuinely tries. Init-only, for binary compatibility.</para>
    /// </summary>
    public IReadOnlyCollection<Mesh.FallbackModule> FallbackModules { get; init; } = [];

    /// <summary>
    /// The current report. Recomputed per call — the state changes underneath a running process
    /// (that is the whole point), so a cached answer would be wrong exactly when it matters.
    /// </summary>
    public ModuleActivationReport Read() => Read(
        ModuleActivationStatus.LoadedAssemblyNames(),
        ModuleActivationStatus.LoadedModuleGenerations());

    /// <summary>
    /// How many times this instance has actually read the activation state off the volume — the
    /// sidecar, every <c>activation.d/*.json</c>, the set index, and one existence probe per landed
    /// DLL. A measurement for the tests that pin <see cref="Read()"/>'s cost: it climbs by one per
    /// CHANGE of the on-disk state, never per call.
    /// </summary>
    public int DiskReads => Volatile.Read(ref diskReads);

    private int diskReads;

    // 🚨 #3664 — the report is read on EVERY startup/readiness probe, and every read walked the
    // volume: ModuleActivationSidecar.Read opens activation.json and each activation.d/*.json,
    // ModuleSetStore.Read lists and parses sets/*.json, and the projection then asks
    // File.Exists once per entry in three passes. On memex-cloud's shared Azure Files volume
    // (714 module generations, 33,383 files) that is 8–10 s per probe against a 5 s probe timeout,
    // so a fresh pod NEVER passed its startup probe — image-independent, every rollout stalled.
    //
    // The state this reads changes only when something LANDS or a wave is PROPOSED, and every
    // writer in this assembly lands its file by temp-file + rename INTO one of three directories
    // (modules/, modules/activation.d/, modules/sets/) or creates/deletes a marker there — each of
    // which moves that directory's last-write time on every filesystem the mesh runs on. So the
    // disk-derived half of the report is memoised behind a fingerprint of those three
    // timestamps (three stats per probe), and only the cheap in-process half — which assemblies
    // THIS process has loaded — is recomputed per call. The snapshot is an immutable record swapped
    // atomically on an instance field: two concurrent probes may compute it twice, both correctly;
    // no lock, no timer, nothing to clear.
    private DiskSnapshot? snapshot;

    /// <summary>The disk-derived inputs of one report, valid while <see cref="Fingerprint"/> stands.</summary>
    private sealed record DiskSnapshot(
        DiskFingerprint Fingerprint,
        ModuleActivationList? Activation,
        string? Corrupt,
        Exception? OpenFailure,
        ModuleSetIndex Sets,
        ImmutableList<string> SetNotes,
        Func<ModuleActivationEntry, bool> LandedDllExists);

    /// <summary>
    /// The last-write times of the three directories every activation writer renames into. PURE
    /// over the file system; a missing directory reads as <see cref="DateTime.MinValue"/>, so the
    /// first landing into it changes the fingerprint too.
    /// </summary>
    public readonly record struct DiskFingerprint(DateTime Modules, DateTime Entries, DateTime Sets)
    {
        public static DiskFingerprint Of(string baseDirectory) => new(
            LastWrite(Path.Combine(baseDirectory, "modules")),
            LastWrite(ModuleActivationSidecar.EntriesDirectory(baseDirectory)),
            LastWrite(ModuleSetStore.SetsDirectory(baseDirectory)));

        private static DateTime LastWrite(string directory) =>
            Directory.Exists(directory) ? Directory.GetLastWriteTimeUtc(directory) : DateTime.MinValue;
    }

    private DiskSnapshot ReadDisk()
    {
        var fingerprint = DiskFingerprint.Of(ModuleRootPath);
        var current = snapshot;
        if (current is not null && current.Fingerprint == fingerprint)
            return current;

        Interlocked.Increment(ref diskReads);
        string? corrupt = null;
        ModuleActivationList? activation = null;
        Exception? openFailure = null;
        try
        {
            // 🚨 The corruption callback is not optional here. ModuleActivationSidecar.Read
            // swallows an unparseable file into the EMPTY list, so a surface that ignores the
            // callback reports a corrupt sidecar as "nothing pending" — cheerfully, forever. That
            // is the shape this whole cluster of defects has in common.
            activation = ModuleActivationSidecar.Read(ModuleRootPath, reason => corrupt = reason);
        }
        catch (Exception exception)
        {
            openFailure = exception;
        }
        var setNotes = ImmutableList.CreateBuilder<string>();
        var sets = openFailure is null && corrupt is null
            ? ModuleSetStore.Read(ModuleRootPath, setNotes.Add)
            : ModuleSetIndex.Empty;
        // One existence probe per landed DLL for the life of this snapshot: the three projection
        // passes below ask about the same entries, and the answer cannot change while the
        // fingerprint stands (a landing renames into activation.d, which moves it).
        var landed = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        bool LandedDllExists(ModuleActivationEntry entry) =>
            landed.GetOrAdd(
                ModuleActivationBoot.LandedDllPath(ModuleRootPath, entry),
                path => File.Exists(path));
        var fresh = new DiskSnapshot(
            fingerprint, activation, corrupt, openFailure, sets, setNotes.ToImmutable(), LandedDllExists);
        snapshot = fresh;
        return fresh;
    }

    /// <summary>
    /// Testable form: the caller supplies what counts as loaded, BY NAME ONLY.
    ///
    /// <para>🚨 Name-only cannot see an UPDATE (#3395) — a module whose activated generation moved
    /// while an older one stays loaded here has its name in this set and reads as not pending. Kept
    /// so a host compiled against the previous platform keeps working; every live caller should
    /// pass the generation map instead.</para>
    /// </summary>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    public ModuleActivationReport Read(IReadOnlySet<string> loadedAssemblyNames) =>
        Read(loadedAssemblyNames, ImmutableDictionary<string, string>.Empty);

    /// <summary>
    /// Testable form: the caller supplies what counts as loaded, by name AND by the generation
    /// directory each module was loaded from (#3395).
    /// </summary>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="loadedModuleGenerations">Module simple name → the generation directory leaf it
    /// was loaded from here; an absent name means "unknown", never "stale".</param>
    public ModuleActivationReport Read(
        IReadOnlySet<string> loadedAssemblyNames,
        IReadOnlyDictionary<string, string> loadedModuleGenerations)
    {
        var disk = ReadDisk();
        if (disk.OpenFailure is { } exception)
            return new ModuleActivationReport(
                [], $"the activation sidecar under '{ModuleRootPath}' could not be opened "
                    + $"({exception.GetType().Name}: {exception.Message})");
        if (disk.Corrupt is not null)
            return new ModuleActivationReport([], disk.Corrupt);
        var activation = disk.Activation!;
        // The SAME existence gate boot applies (see ReadDisk): this report PROMISES that a restart
        // activates what it calls pending, and only the gate boot itself uses can keep that
        // promise. A landed DLL that is gone (#2093) is reported SEPARATELY rather than dropped.
        var LandedDllExists = disk.LandedDllExists;
        var setNotes = disk.SetNotes;
        var sets = disk.Sets;

        var deferred = ImmutableList.CreateBuilder<PendingModuleActivation>();
        var onMeshSet = ModuleActivationBoot.ProjectOntoMeshSet(
            activation,
            sets.Proposed,
            (module, _) => deferred.Add(new PendingModuleActivation(
                module,
                activation.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase))?.PackagePath,
                activation.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase))?.Version)),
            // The same existence check boot passes, so this report describes the set boot would
            // actually load rather than the one on paper.
            LandedDllExists);

        var notYetLoaded = ModuleActivationStatus.NotYetLoaded(
            onMeshSet, loadedAssemblyNames, loadedModuleGenerations,
            ModulePlatformFloor.DeclineReason, LandedDllExists);

        // 🚨 #3538 — a module this process REFUSED to load is not pending, it is quarantined. Its
        // assembly is genuinely absent from the loaded set, so the pending derivation above finds
        // it and would promise a restart; a restart re-runs the same measurement on the same bytes
        // and refuses again. Same false-promise rule as a held entry and a missing DLL, one state
        // further on — and the only one whose remedy is a PLATFORM update rather than an operator
        // action.
        ImmutableList<PendingModuleActivation> quarantined = QuarantinedModules.Count == 0
            ? []
            : [.. notYetLoaded.Where(p => QuarantinedModules.Contains(p.Name))];

        // 🚨 #3649 — a module running its PREVIOUS generation is not pending either: its entry on
        // the mesh's set names the generation the loader REFUSED, so a restart measures the same
        // bytes and falls back again. It becomes pending again only when the set moves on to a
        // generation other than the refused one — that is what a restart would genuinely try,
        // and R3 (#3650) is what makes that restart happen. The row is derived from the entry the
        // set activates, so it carries the install record's path for the package card.
        var fallbackRows = ImmutableList.CreateBuilder<ModuleFallback>();
        var settledFallbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fallback in FallbackModules)
        {
            var entry = onMeshSet.Entries.FirstOrDefault(e =>
                e.Enabled && string.Equals(e.Name, fallback.Name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;
            fallbackRows.Add(new ModuleFallback(
                fallback.Name, entry.PackagePath,
                fallback.Version ?? entry.Version, fallback.Generation,
                fallback.PreviousVersion ?? entry.PreviousVersion, fallback.PreviousGeneration,
                fallback.Describe()));
            if (string.Equals(entry.Directory, fallback.Generation, StringComparison.Ordinal))
                settledFallbacks.Add(fallback.Name);
        }
        if (settledFallbacks.Count > 0)
            notYetLoaded = [.. notYetLoaded.Where(p => !settledFallbacks.Contains(p.Name))];

        // 🚨 #3648 — the declared floor is ADVISORY, so it is a LINE, never a bucket. Every enabled
        // entry the mesh's set activates whose recorded minMeshVersion ranks above the running
        // platform is listed here — loaded or not — so the status row and the health payload can
        // say "declares platform ≥ X; running Y" beside pending/quarantined, never instead of them.
        var floorAdvisories = onMeshSet.Entries
            .Where(entry => entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.Name)
                && !string.IsNullOrWhiteSpace(entry.MinMeshVersion))
            .Select(entry => (Entry: entry, Reason: ModulePlatformFloor.DeclineReason(entry.MinMeshVersion)))
            .Where(pair => pair.Reason is not null)
            .Select(pair => new ModuleFloorAdvisory(
                pair.Entry.Name, pair.Entry.PackagePath, pair.Entry.MinMeshVersion!,
                ModulePlatformFloor.RunningVersion, pair.Reason!))
            .ToImmutableList();

        return new ModuleActivationReport(
            quarantined.IsEmpty
                ? notYetLoaded
                : [.. notYetLoaded.Where(p => !QuarantinedModules.Contains(p.Name))],
            UndeterminedReason: null,
            ModuleActivationStatus.Unresolvable(
                onMeshSet, loadedAssemblyNames, loadedModuleGenerations,
                ModulePlatformFloor.DeclineReason, LandedDllExists))
        {
            Deferred = deferred.ToImmutable(),
            Quarantined = quarantined,
            Fallbacks = fallbackRows.ToImmutable(),
            FloorAdvisories = floorAdvisories,
            MeshModuleSet = ModuleSetStore.Describe(sets)
                + (setNotes.Count > 0 ? " — " + string.Join("; ", setNotes) : string.Empty),
        };
    }

    /// <summary>
    /// Whether the install record at <paramref name="packagePath"/> landed a module that has not
    /// loaded here.
    ///
    /// <para>🚨 Returns <c>false</c> when the state is UNDETERMINED, and that is deliberate: this
    /// answers a per-package question whose only honest fallback is "I have nothing to say about
    /// this package". The undetermined case is reported by the operator surface, which is where an
    /// unreadable sidecar is actionable — putting "restart required" on every package card because
    /// a file could not be parsed would be noise a buyer cannot act on.</para>
    /// </summary>
    public bool IsPendingForPackage(string? packagePath) => Read().IsPendingForPackage(packagePath);
}
