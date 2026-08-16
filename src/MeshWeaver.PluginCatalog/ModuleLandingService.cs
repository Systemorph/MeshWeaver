using System.IO;
using System.Reactive;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The runtime writer into <c>modules/</c> (#1664 step 7) — the ONE code path that lands a
/// compiled module's assemblies beside the app at runtime and records its activation, so the next
/// restart loads it (restart-as-activation, #1664 step 8). This is the seam Slice C's
/// <c>PackageInstaller</c> binary branch calls after fetching a module bundle; nothing in Slice A
/// invokes it from the install funnel yet.
///
/// <para><b>The MVID gate holds at placement.</b> Landing verifies the caller's declared
/// framework identity through <see cref="PrebuiltAssemblySeeder.DeclineReason"/> — the SAME pure
/// function the prebuilt-assembly seeder and the bundle client gate on, so there is never a
/// second notion of framework version. A mismatch REFUSES the landing (the observable errors,
/// naming both MVIDs); declined bytes never reach disk.</para>
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

    /// <summary>
    /// Lands a module: verifies the framework MVID, writes the assemblies atomically into
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
    /// assemblies were built against, as recorded by the producer.</param>
    /// <param name="packagePath">The install record's mesh path, when the store lane calls.</param>
    public IObservable<Unit> LandModule(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string frameworkMvid,
        string? packagePath = null)
        => pool.InvokeBlocking(_ =>
        {
            LandCore(name, assemblies, frameworkMvid, packagePath);
            return Unit.Default;
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
        });

    private void LandCore(
        string name,
        IReadOnlyList<(string FileName, byte[] Bytes)> assemblies,
        string frameworkMvid,
        string? packagePath)
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

        // 🚨 THE MVID GATE, at placement — the same pure function PrebuiltAssemblySeeder and
        // PluginBundleClient gate on. Declining is always safe; landing on faith is not: the
        // ABI mismatch would surface only at the next boot, as a TypeLoadException with nothing
        // connecting it to the install that caused it.
        if (PrebuiltAssemblySeeder.DeclineReason(frameworkMvid) is { } reason)
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
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
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

        var list = ModuleActivationSidecar.Read(baseDirectory,
            msg => logger?.LogError("{Message}", msg));
        var entry = new ModuleActivationEntry
        {
            Name = name,
            Source = ModuleActivationSources.Store,
            PackagePath = packagePath,
            FrameworkMvid = frameworkMvid,
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
            "Module '{Name}' LANDED into modules/{Name}/ ({Count} assemblies, framework "
            + "{FrameworkMvid}) — activation recorded, RESTART REQUIRED to load it",
            name, name, assemblies.Count, frameworkMvid);
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
