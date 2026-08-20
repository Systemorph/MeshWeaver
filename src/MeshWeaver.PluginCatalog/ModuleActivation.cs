using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Where a module-activation entry came from — the two lanes of #1664 step 9.
/// </summary>
public static class ModuleActivationSources
{
    /// <summary>The deployment's <c>Modules:Assemblies</c> appsettings baseline.</summary>
    public const string AppSettings = "appsettings";

    /// <summary>A Store install that landed the module via <see cref="ModuleLandingService"/>.</summary>
    public const string Store = "store";
}

/// <summary>
/// One activated (or deliberately deactivated) module in the persisted activation list —
/// the durable record that replaces "edit appsettings and redeploy" for store-installed modules
/// (#1664 step 9).
/// </summary>
public sealed record ModuleActivationEntry
{
    /// <summary>The module's DLL name WITHOUT extension (e.g. <c>MeshWeaver.Markdown.Export</c>)
    /// — the same identity <c>MeshBuilder.ResolveModulePath</c> probes <c>modules/&lt;name&gt;/</c>
    /// with.</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="ModuleActivationSources"/>. Sidecar entries are written by the
    /// store lane; the appsettings baseline never round-trips through this file.</summary>
    public string Source { get; init; } = ModuleActivationSources.Store;

    /// <summary>The mesh path of the install record (Package node) that landed this module, when
    /// the store lane wrote it — the back-pointer Slice C's funnel uses.</summary>
    public string? PackagePath { get; init; }

    /// <summary>
    /// The GENERATION directory under <c>modules/</c> this entry's bytes live in
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>). Landing writes every version into a FRESH generation and
    /// moves this pointer — nothing on the landing path ever deletes or overwrites a directory a
    /// running pod may hold open, which is what made delete-based swaps unsafe on a shared volume
    /// (2026-08-20: a rolling restart's boot-time applies half-deleted 13 of 15 module closures).
    /// Absent → the legacy fixed folder <c>modules/&lt;name&gt;/</c>. Unreferenced generations are
    /// garbage-collected at boot, skip-on-locked.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>The framework MVID (MeshWeaver.Graph's ModuleVersionId) the landed assemblies
    /// were built against, as the producer recorded it — DIAGNOSTIC metadata only: it names the
    /// exact build behind the bytes when something needs debugging, but it is never a gate.
    /// Modules bind by simple name and their contract is API compatibility, expressed by
    /// <see cref="MinMeshVersion"/>; the strict MVID gate is bake semantics and belongs to the
    /// NodeType assembly lane.</summary>
    public string? FrameworkMvid { get; init; }

    /// <summary>The module's declared platform FLOOR (<c>minMeshVersion</c>) as recorded at
    /// landing. Boot SKIPS the entry — loudly, never a crash — when the running platform no
    /// longer satisfies it (a rollback below the floor); absent = no constraint. The one gate is
    /// <see cref="ModulePlatformFloor.DeclineReason(string?)"/>.</summary>
    public string? MinMeshVersion { get; init; }

    /// <summary>The package version the landed bundle was served at (the module package's released
    /// SemVer). What the auto-update reconcile compares against the registry's bundle index to
    /// decide "already landed" without downloading a byte (<see cref="ModuleUpdateDecision"/>).
    /// Null on an entry written before this field existed — which reads as "unknown", so the next
    /// reconcile re-lands once and records it.</summary>
    public string? Version { get; init; }

    /// <summary>False = uninstalled (the record is kept for history/idempotence; the folder is
    /// deleted). Takes effect at the next restart, like every activation change.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// The persisted per-deployment module-activation list — the content of the
/// <c>modules/activation.json</c> sidecar (see <see cref="ModuleActivationSidecar"/>).
/// </summary>
public sealed record ModuleActivationList
{
    /// <summary>The activation entries, in landing order.</summary>
    public ImmutableList<ModuleActivationEntry> Entries { get; init; } = [];

    /// <summary>True when an activation change (install/uninstall) has landed since the last
    /// restart — the minimal #1664 step-10 "restart required" signal. Boot consumes it: applying
    /// the list IS the restart, so <c>ConfigureMemexMesh</c> resets it to false.</summary>
    public bool PendingRestart { get; init; }
}

/// <summary>
/// Plain-file persistence of the module-activation list: <c>modules/activation.json</c>, beside
/// the module folders it describes.
///
/// <para><b>Why a sidecar file and not a mesh node:</b> the list is consumed at BOOT, in
/// <c>ConfigureMemexMesh</c>, BEFORE the DI container exists — before any storage provider is
/// registered, before any hub runs, and (on PG) before a connection string has been validated.
/// A mesh-node read at that point would need a parallel pre-DI storage bootstrap; a file beside
/// the folders it activates needs <c>File.ReadAllText</c>. It also cannot drift from the DLLs:
/// the landing service writes both in the same operation onto the same volume, so a
/// restore/copy of the deployment's file tree carries both or neither.</para>
///
/// <para>All IO here is plain and synchronous by design — boot-time (pre-DI, pre-IoPool) callers
/// use it directly; runtime callers go through <see cref="ModuleLandingService"/>, which runs
/// these on its bounded IO pool.</para>
/// </summary>
public static class ModuleActivationSidecar
{
    /// <summary>The sidecar's file name inside the <c>modules/</c> folder.</summary>
    public const string FileName = "activation.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The sidecar's full path for a deployment rooted at
    /// <paramref name="baseDirectory"/> (normally <c>AppContext.BaseDirectory</c>).</summary>
    public static string SidecarPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "modules", FileName);

    /// <summary>
    /// Reads the activation list. A missing file is the normal fresh-deployment state and reads
    /// as the empty list; a CORRUPT file reads as the empty list too — the deployment must boot
    /// (baseline modules unaffected) — but reports through <paramref name="onCorrupt"/> so the
    /// skip is loud, never silent.
    /// </summary>
    public static ModuleActivationList Read(string baseDirectory, Action<string>? onCorrupt = null)
    {
        var path = SidecarPath(baseDirectory);
        if (!File.Exists(path))
            return new ModuleActivationList();
        try
        {
            return JsonSerializer.Deserialize<ModuleActivationList>(File.ReadAllText(path), Json)
                   ?? new ModuleActivationList();
        }
        catch (Exception ex)
        {
            onCorrupt?.Invoke(
                $"Module activation sidecar '{path}' could not be read ({ex.GetType().Name}: "
                + $"{ex.Message}) — booting with the appsettings baseline only. Store-installed "
                + "modules will NOT load until the sidecar is repaired or the modules are "
                + "re-installed.");
            return new ModuleActivationList();
        }
    }

    /// <summary>
    /// Writes the activation list atomically: serialize to a temp file in the same directory,
    /// then rename over the target — a crash mid-write can never leave a half-written sidecar.
    /// </summary>
    public static void Write(string baseDirectory, ModuleActivationList list)
    {
        var path = SidecarPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// One module the boot union activates, with the LANE it came from.
/// </summary>
/// <param name="Entry">The <c>Modules:Assemblies</c>-shaped entry (<c>&lt;Name&gt;.dll</c> for a
/// sidecar entry; the operator's raw value for a baseline one).</param>
/// <param name="Landed">The sidecar entry this came from, or <c>null</c> when it came from the
/// appsettings baseline. 🚨 Provenance is carried rather than re-derived: a landed module's bytes
/// live in the directory ITS entry points at, a baseline module's are resolved by probing, and a
/// caller that guesses which lane a name belongs to is how the boot gate and the boot loader came
/// to disagree (#1949).</param>
public sealed record EffectiveModule(string Entry, ModuleActivationEntry? Landed);

/// <summary>
/// The boot-time union of #1664 step 9: appsettings baseline ∪ enabled persisted store installs,
/// deduped by module name, with the two skip rules (missing DLL, unsatisfied platform floor)
/// applied loudly — extracted PURE so the computation is unit-testable without booting a portal.
/// </summary>
public static class ModuleActivationBoot
{
    /// <summary>
    /// Computes the effective module list the boot loader feeds to
    /// <c>MeshBuilder.InstallAssemblies</c> (after per-module <see cref="ResolveLoadPath"/>).
    ///
    /// <para>Each result carries its PROVENANCE — the sidecar entry it came from, or null for an
    /// appsettings-baseline entry — because the two lanes resolve to a file by different rules and
    /// the caller must not re-derive which lane a name belongs to. That re-derivation is what
    /// #1949 was: the existence gate and the load-path resolver each decided for themselves where
    /// a module's bytes were, disagreed about generation directories, and every store module on
    /// the deployment was skipped at boot while its bytes sat correctly on disk.</para>
    ///
    /// <para>Rules, in order:</para>
    /// <list type="bullet">
    ///   <item>The appsettings <paramref name="baselineEntries"/> pass through UNCHANGED, in
    ///     order — they are the image's own closure and keep today's contract (a baseline entry
    ///     that fails to load fails loudly at startup; the skip rules below are for persisted
    ///     entries only).</item>
    ///   <item>Each ENABLED persisted entry appends as <c>&lt;Name&gt;.dll</c> unless: it
    ///     duplicates a baseline (or earlier persisted) module name — dedupe, silent, the module
    ///     is simply already activated; its recorded <see cref="ModuleActivationEntry.MinMeshVersion"/>
    ///     floor is refused by <paramref name="platformGate"/> (the platform rolled BACK below
    ///     the module's declared requirement — SKIPPED with a loud report, the entry stays for
    ///     when the platform moves forward again); or its LANDED DLL is missing per
    ///     <paramref name="landedModuleDllExists"/> (a lost volume / manual deletion — SKIPPED
    ///     loudly; a same-named app-closure DLL does not count). A skip is never a crash: the
    ///     deployment must boot. A landed module's MVID is deliberately NOT a skip condition —
    ///     modules bind by simple name across platform builds; the recorded MVID is diagnostic.</item>
    ///   <item>Disabled entries (uninstalled) contribute nothing and report nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="baselineEntries">The raw <c>Modules:Assemblies</c> values (may be null/empty).</param>
    /// <param name="persisted">The sidecar list (may be null).</param>
    /// <param name="platformGate">Returns WHY a recorded platform FLOOR is not satisfied by the
    /// running platform, or null when it is (an absent floor is always satisfied) — production
    /// passes <see cref="ModulePlatformFloor.DeclineReason(string?)"/> so there is never a second
    /// notion of the module platform requirement.</param>
    /// <param name="landedModuleDllExists">Whether a persisted module's LANDED entry DLL exists —
    /// called with the ENTRY, never with the bare name, because the entry is what says WHERE its
    /// bytes are: <see cref="ModuleActivationEntry.Directory"/> names the generation directory
    /// landing wrote them into. 🚨 It must check that ONE directory
    /// (<c>modules/&lt;entry.Directory ?? name&gt;/&lt;name&gt;.dll</c> — production passes
    /// <see cref="LandedModuleDllExists"/>), never <c>MeshBuilder.ResolveModulePath</c>: that
    /// resolver falls back to the app's base directory, so a sidecar entry whose landed folder is
    /// gone (tampered sidecar, deleted folder) would silently BIND a same-named app-closure DLL
    /// instead of being skipped. A store-installed module never legitimately lives in the app
    /// closure — a name collision there is exactly what <see cref="ModuleLandingService"/> refuses
    /// at landing.</param>
    /// <param name="onSkipped">The loud channel: called once per skipped persisted entry with
    /// (module name, reason).</param>
    public static ImmutableList<EffectiveModule> ComputeEffectiveModuleEntries(
        IReadOnlyList<string>? baselineEntries,
        ModuleActivationList? persisted,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedModuleDllExists,
        Action<string, string>? onSkipped = null)
    {
        var effective = ImmutableList.CreateBuilder<EffectiveModule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baselineEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            effective.Add(new EffectiveModule(entry, Landed: null));
            seen.Add(Path.GetFileNameWithoutExtension(entry));
        }

        foreach (var module in persisted?.Entries ?? [])
        {
            if (!module.Enabled || string.IsNullOrWhiteSpace(module.Name))
                continue;
            if (!seen.Add(module.Name))
                continue; // already activated (baseline or an earlier entry) — dedupe by name

            if (platformGate(module.MinMeshVersion) is { } reason)
            {
                onSkipped?.Invoke(module.Name, reason);
                continue;
            }

            if (!landedModuleDllExists(module))
            {
                onSkipped?.Invoke(module.Name,
                    $"its landed DLL '{RelativeLandedDll(module)}' does not exist "
                    + "(folder lost or never landed; a same-named app-closure DLL deliberately "
                    + "does NOT satisfy a store-installed entry) — re-install the module");
                continue;
            }

            effective.Add(new EffectiveModule(module.Name + ".dll", module));
        }

        return effective.ToImmutable();
    }

    /// <summary>
    /// The landed entry-DLL path of a store-installed entry:
    /// <c>{baseDirectory}/modules/{entry.Directory ?? entry.Name}/{entry.Name}.dll</c>.
    ///
    /// <para>🚨 The directory comes from the ENTRY, through the ONE resolution rule
    /// (<see cref="ModuleLandingService.ModuleDirectoryFor"/>) the serve side and the boot loader
    /// already share. Landing writes every version into a FRESH generation
    /// (<c>modules/&lt;name&gt;@&lt;id&gt;/</c>) and moves this pointer; a check that looked in the
    /// legacy fixed <c>modules/&lt;name&gt;/</c> folder would find NOTHING for any
    /// generation-landed module — which is exactly how every store module on a
    /// generation-landing build came up skipped at boot while its bytes sat correctly on disk
    /// (#1949). The gate and the resolver must name ONE path or landing and activation never
    /// converge.</para>
    /// </summary>
    public static string LandedDllPath(string baseDirectory, ModuleActivationEntry entry) =>
        Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(baseDirectory, entry.Name, entry),
            entry.Name + ".dll");

    /// <summary>
    /// The PRODUCTION existence check for a persisted (store-installed) entry — the landed DLL at
    /// <see cref="LandedDllPath"/>, and nothing else. 🚨 Deliberately NOT
    /// <c>MeshBuilder.ResolveModulePath</c>: that resolver falls back to the app's base directory,
    /// which is correct for appsettings-baseline entries (both locations are legitimate for them)
    /// but would let a sidecar entry whose landed folder is gone silently bind a same-named
    /// app-closure DLL instead of being skipped — a store-installed module never legitimately
    /// lives in the app closure (the landing service refuses that collision).
    /// </summary>
    public static bool LandedModuleDllExists(string baseDirectory, ModuleActivationEntry entry) =>
        File.Exists(LandedDllPath(baseDirectory, entry));

    /// <summary>
    /// The file the boot loader loads for one <see cref="EffectiveModule"/> — the SAME resolution
    /// the existence gate applied, which is the whole point of it living here:
    ///
    /// <list type="bullet">
    ///   <item>a SIDECAR (store-landed) module resolves to <see cref="LandedDllPath"/> — its own
    ///     landed directory and nothing else. Never <c>ResolveModulePath</c>: its app-closure
    ///     fallback would bind a same-named platform binary for an entry the gate just decided
    ///     was present, which is the trap-door <see cref="ModuleLandingService"/> refuses at
    ///     landing.</item>
    ///   <item>a BASELINE entry keeps <c>MeshBuilder.ResolveModulePath</c>'s probes (landed root's
    ///     <c>modules/&lt;name&gt;/</c>, the image's own, then the classic BaseDirectory-relative
    ///     location) — all of them legitimate for the image's own closure.</item>
    /// </list>
    /// </summary>
    public static string ResolveLoadPath(string baseDirectory, EffectiveModule module) =>
        module.Landed is not null
            ? LandedDllPath(baseDirectory, module.Landed)
            : MeshBuilder.ResolveModulePath(module.Entry, baseDirectory);

    /// <summary>The entry's landed DLL as the deployment-relative path a skip report names —
    /// the generation directory when the entry carries one, else the legacy fixed folder.</summary>
    private static string RelativeLandedDll(ModuleActivationEntry entry) =>
        $"modules/{(string.IsNullOrWhiteSpace(entry.Directory) ? entry.Name : entry.Directory)}"
        + $"/{entry.Name}.dll";
}
