using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>The framework MVID (MeshWeaver.Graph's ModuleVersionId) the landed assemblies
    /// were built against, as verified at landing time. Boot SKIPS the entry — loudly, never a
    /// crash — when this does not match the running framework: after an image roll the landed
    /// bytes are ABI-stale, and the entry waits for a re-install against the new framework.</summary>
    public string? FrameworkMvid { get; init; }

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
/// The boot-time union of #1664 step 9: appsettings baseline ∪ enabled persisted store installs,
/// deduped by module name, with the two skip rules (missing DLL, framework-MVID mismatch)
/// applied loudly — extracted PURE so the computation is unit-testable without booting a portal.
/// </summary>
public static class ModuleActivationBoot
{
    /// <summary>
    /// Computes the effective <c>Modules:Assemblies</c>-shaped entry list the boot loader feeds
    /// to <c>MeshBuilder.InstallAssemblies</c> (after per-entry <c>ResolveModulePath</c>).
    ///
    /// <para>Rules, in order:</para>
    /// <list type="bullet">
    ///   <item>The appsettings <paramref name="baselineEntries"/> pass through UNCHANGED, in
    ///     order — they are the image's own closure and keep today's contract (a baseline entry
    ///     that fails to load fails loudly at startup; the skip rules below are for persisted
    ///     entries only).</item>
    ///   <item>Each ENABLED persisted entry appends as <c>&lt;Name&gt;.dll</c> unless: it
    ///     duplicates a baseline (or earlier persisted) module name — dedupe, silent, the module
    ///     is simply already activated; its recorded <see cref="ModuleActivationEntry.FrameworkMvid"/>
    ///     is refused by <paramref name="frameworkGate"/> (an image roll changed the framework —
    ///     SKIPPED with a loud report, the entry stays for the post-roll re-install); or its DLL
    ///     is missing per <paramref name="entryDllExists"/> (a lost volume / manual deletion —
    ///     SKIPPED loudly). A skip is never a crash: the deployment must boot.</item>
    ///   <item>Disabled entries (uninstalled) contribute nothing and report nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="baselineEntries">The raw <c>Modules:Assemblies</c> values (may be null/empty).</param>
    /// <param name="persisted">The sidecar list (may be null).</param>
    /// <param name="frameworkGate">Returns WHY a recorded framework identity may not load against
    /// the running framework, or null when it may — production passes
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c> so there is never a second notion of framework
    /// identity.</param>
    /// <param name="entryDllExists">Whether an entry (e.g. <c>Foo.dll</c>) resolves to an existing
    /// file — production passes <c>File.Exists(MeshBuilder.ResolveModulePath(entry))</c>.</param>
    /// <param name="onSkipped">The loud channel: called once per skipped persisted entry with
    /// (module name, reason).</param>
    public static ImmutableList<string> ComputeEffectiveModuleEntries(
        IReadOnlyList<string>? baselineEntries,
        ModuleActivationList? persisted,
        Func<string?, string?> frameworkGate,
        Func<string, bool> entryDllExists,
        Action<string, string>? onSkipped = null)
    {
        var effective = ImmutableList.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baselineEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            effective.Add(entry);
            seen.Add(Path.GetFileNameWithoutExtension(entry));
        }

        foreach (var module in persisted?.Entries ?? [])
        {
            if (!module.Enabled || string.IsNullOrWhiteSpace(module.Name))
                continue;
            if (!seen.Add(module.Name))
                continue; // already activated (baseline or an earlier entry) — dedupe by name

            if (frameworkGate(module.FrameworkMvid) is { } reason)
            {
                onSkipped?.Invoke(module.Name, reason);
                continue;
            }

            var entry = module.Name + ".dll";
            if (!entryDllExists(entry))
            {
                onSkipped?.Invoke(module.Name,
                    $"its DLL '{entry}' does not resolve to an existing file (modules/"
                    + $"{module.Name}/ lost or never landed) — re-install the module");
                continue;
            }

            effective.Add(entry);
        }

        return effective.ToImmutable();
    }
}
