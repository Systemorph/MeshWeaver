using System.IO;
using System.Reactive;
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
/// <see cref="IoPool"/> — the one sanctioned bounded-IO primitive — so concurrent landings (and
/// their sidecar read-modify-writes) serialize without a hand-rolled gate, and nothing blocks a
/// hub scheduler. Mesh-scoped singleton: the pool dies with the mesh.</para>
/// </summary>
public sealed class ModuleLandingService : IDisposable
{
    /// <summary>The deferred-swap folder for a module (see the re-land fallback in
    /// <see cref="LandModule"/>). Pure.</summary>
    public static string PendingPathFor(string baseDirectory, string moduleName) =>
        Path.Combine(baseDirectory, "modules", ".pending-" + moduleName);

    /// <summary>
    /// Applies every deferred landing under <c>modules/.pending-*</c> — the boot half of the
    /// re-land fallback. MUST run before any module assembly is loaded: at that moment no file
    /// is open, so the delete the running instance could not perform succeeds. A failed apply
    /// logs, leaves the pending folder for the next boot, and the OLD module folder keeps
    /// loading — stale but working, never a boot failure.
    /// </summary>
    /// <returns>How many pending landings were applied.</returns>
    public static int ApplyPendingLandings(string baseDirectory, ILogger? logger = null)
    {
        var modulesRoot = Path.Combine(baseDirectory, "modules");
        if (!Directory.Exists(modulesRoot))
            return 0;
        var applied = 0;
        foreach (var pending in Directory.EnumerateDirectories(modulesRoot, ".pending-*"))
        {
            var name = Path.GetFileName(pending)[".pending-".Length..];
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var target = Path.Combine(modulesRoot, name);
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(pending, target);
                applied++;
                logger?.LogInformation(
                    "Module '{Name}': deferred landing applied at boot ({Target})", name, target);
            }
            catch (Exception e)
            {
                logger?.LogError(e,
                    "Module '{Name}': deferred landing could NOT be applied — the previous copy "
                    + "keeps loading; the pending folder stays for the next boot", name);
            }
        }
        return applied;
    }

    // Cap 1 deliberately: LandModule/RemoveModule each read-modify-write the shared
    // activation.json sidecar, and the cap IS the serialization — no lock, no semaphore
    // outside the sealed IoPool primitive. Landing is rare and short; 1 costs nothing.
    private readonly IoPool pool = new(1);
    private readonly string baseDirectory;
    private readonly ILogger<ModuleLandingService>? logger;

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
        string? minMeshVersion = null)
        => pool.InvokeBlocking(_ =>
        {
            LandCore(name, assemblies, frameworkMvid, packagePath, version, minMeshVersion);
            return Unit.Default;
        });

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
        });

    private void LandCore(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string? frameworkMvid,
        string? packagePath,
        string? version,
        string? minMeshVersion)
    {
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

        // 🚨 THE PLATFORM-FLOOR GATE, at placement — the same pure function the serve and fetch
        // sides gate on (ModulePlatformFloor), so there is never a second notion of the module
        // platform requirement. Deliberately NOT MVID equality: modules bind by simple name and
        // their contract is API compatibility — the strict MVID gate is bake semantics and stays
        // with the NodeType lane. Declining an unsatisfied floor is always safe; landing on faith
        // is not: the missing API would surface only at the next boot, as a
        // MissingMethodException with nothing connecting it to the install that caused it.
        if (ModulePlatformFloor.DeclineReason(minMeshVersion) is { } reason)
        {
            logger?.LogWarning("Module '{Name}' REFUSED at landing: {Reason}", name, reason);
            throw new InvalidOperationException($"Module '{name}' refused: {reason}");
        }

        // 🚨 The same-identity trap-door: modules/<name>/<name>.dll wins over the app folder in
        // ResolveModulePath, so a module named after an app-closure assembly would shadow the
        // platform's own binary on the next boot.
        if (File.Exists(Path.Combine(baseDirectory, entryDll)))
            throw new InvalidOperationException(
                $"Module '{name}' refused: '{entryDll}' is part of the application closure, and "
                + $"modules/{name}/ would SHADOW it at the next boot "
                + "(ResolveModulePath probes the modules folder first).");

        var modulesRoot = Path.Combine(baseDirectory, "modules");
        var target = Path.Combine(modulesRoot, name);
        var staging = Path.Combine(modulesRoot, $".staging-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (fileName, bytes) in assemblies)
                File.WriteAllBytes(Path.Combine(staging, fileName), bytes);
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
                Directory.Move(staging, target);
            }
            catch (Exception swap) when (swap is IOException or UnauthorizedAccessException)
            {
                // 🚨 The target's files are OPEN — this instance has the module LOADED, and on an
                // SMB-backed volume (Azure Files) an open file cannot be deleted, so the in-place
                // swap fails with "Directory not empty". That is the RE-LAND-ONTO-A-RUNNING-
                // REGISTRY case (first hit 2026-08-20: every re-published module 409'd the moment
                // the portal actually loaded its modules). The bytes are not lost and the publish
                // must not fail: they park as modules/.pending-<name>/, the activation entry still
                // flips PendingRestart, and ApplyPendingLandings swaps them in at the next boot —
                // BEFORE anything is loaded, when the delete cannot be refused. The serving side
                // prefers the pending folder, so consumers fetch the NEW bytes immediately even
                // while this process still runs the old ones.
                var pending = PendingPathFor(baseDirectory, name);
                if (Directory.Exists(pending))
                    Directory.Delete(pending, recursive: true);
                Directory.Move(staging, pending);
                logger?.LogWarning(
                    "Module '{Name}': the loaded copy's files are open, so the swap is DEFERRED — "
                    + "staged at {Pending}; applies at the next restart ({Reason})",
                    name, pending, swap.Message);
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

        var list = ModuleActivationSidecar.Read(baseDirectory,
            msg => logger?.LogError("{Message}", msg));
        var entry = new ModuleActivationEntry
        {
            Name = name,
            Source = ModuleActivationSources.Store,
            PackagePath = packagePath,
            FrameworkMvid = frameworkMvid,
            Version = version,
            MinMeshVersion = minMeshVersion,
            Enabled = true,
        };
        ModuleActivationSidecar.Write(baseDirectory, list with
        {
            Entries = list.Entries
                .RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                .Add(entry),
            PendingRestart = true,
        });

        logger?.LogInformation(
            "Module '{Name}' LANDED into modules/{Name}/ ({Count} assemblies, floor "
            + "{MinMeshVersion}, platform {Running}; built against framework MVID "
            + "{FrameworkMvid} — diagnostic) — activation recorded, RESTART REQUIRED to load it",
            name, name, assemblies.Count, minMeshVersion ?? "(none)",
            ModulePlatformFloor.RunningVersion ?? "(unknown)", frameworkMvid ?? "(unrecorded)");
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

        ModuleActivationSidecar.Write(baseDirectory, list with
        {
            Entries = list.Entries.Replace(existing, existing with { Enabled = false }),
            PendingRestart = true,
        });

        var target = Path.Combine(baseDirectory, "modules", name);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);

        logger?.LogInformation(
            "Module '{Name}' UNINSTALLED: activation disabled, modules/{Name}/ deleted — "
            + "takes effect at the next restart", name, name);
    }

    private static void ValidateFileName(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException($"Invalid {what}: '{value}'.");
    }

    /// <inheritdoc />
    public void Dispose() => pool.Dispose();
}
