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
              + (HasQuarantined ? "; " + DescribeQuarantined(Quarantined) : string.Empty);

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
    /// The current report. Recomputed per call — the state changes underneath a running process
    /// (that is the whole point), so a cached answer would be wrong exactly when it matters.
    /// </summary>
    public ModuleActivationReport Read() => Read(
        ModuleActivationStatus.LoadedAssemblyNames(),
        ModuleActivationStatus.LoadedModuleGenerations());

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
        string? corrupt = null;
        ModuleActivationList activation;
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
            return new ModuleActivationReport(
                [], $"the activation sidecar under '{ModuleRootPath}' could not be opened "
                    + $"({exception.GetType().Name}: {exception.Message})");
        }

        if (corrupt is not null)
            return new ModuleActivationReport([], corrupt);

        // 🚨 The SAME two gates boot applies, threaded here for the same reason: this report
        // PROMISES that a restart activates what it calls pending, and only the gates boot itself
        // uses can keep that promise. The platform floor (a HELD entry — the registry shelf,
        // 2026-08-22) and the landed DLL's existence (#2093) each mean boot would skip the entry.
        // The second is reported SEPARATELY rather than dropped: an activated module whose bytes
        // are gone is a fault an operator must act on, not a quiet nothing.
        bool LandedDllExists(ModuleActivationEntry entry) =>
            ModuleActivationBoot.LandedModuleDllExists(ModuleRootPath, entry);

        // 🚨 #3395 — compare against THE MESH'S MODULE SET, which is what a restart of this process
        // would actually load, not against the raw activation record, which is a moving target no
        // boot resolves any more. Getting this wrong in either direction breaks the report's one
        // promise: comparing against the record would call a pod "pending" for a landing whose wave
        // has not completed (a restart would not load it — a promise no restart can keep, the exact
        // false prompt the held-entry and missing-bytes rules exist to prevent), and it is what let
        // a pod 90 minutes behind answer Healthy in the first place.
        // 🚨 The set store's reports are NOT verdicts, and folding them into UndeterminedReason
        // would make them into one. `ModuleSetStore.Read` reports a deterministically-RESOLVED
        // conflict (two replicas proposed one sequence; every reader picks the same set) and a
        // single unreadable record (skipped, the rest stand) through the same channel — both are
        // handled conditions with a valid index behind them. Even the one genuinely blind case, a
        // directory that cannot be listed, answers `Empty`, which projects as the IDENTITY: this
        // pod then reports exactly what it reported before #3395, which is an answer, not an
        // absence of one. So the notes go on the mesh-set LINE, where an operator sees them, and
        // the verdict stays what the activation record supports.
        var setNotes = new List<string>();
        var sets = ModuleSetStore.Read(ModuleRootPath, setNotes.Add);

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
