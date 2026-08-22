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
/// <param name="Pending">The modules landed on the volume but not loaded in this process.</param>
/// <param name="UndeterminedReason">Why the answer is unknown, or <c>null</c> when it is known.
/// When set, <paramref name="Pending"/> is empty and means nothing.</param>
public sealed record ModuleActivationReport(
    ImmutableList<PendingModuleActivation> Pending,
    string? UndeterminedReason = null)
{
    /// <summary>True when this process could not establish the activation state at all.</summary>
    public bool IsUndetermined => UndeterminedReason is not null;

    /// <summary>True when the state is KNOWN and something is waiting on a restart.</summary>
    public bool HasPending => !IsUndetermined && !Pending.IsEmpty;

    /// <summary>The one line every surface renders, so nobody is told two different stories.</summary>
    public string Describe() =>
        UndeterminedReason is { } reason
            ? "module activation state could not be determined — " + reason
            : ModuleActivationStatus.Describe(Pending);
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
    /// The current report. Recomputed per call — the state changes underneath a running process
    /// (that is the whole point), so a cached answer would be wrong exactly when it matters.
    /// </summary>
    public ModuleActivationReport Read() => Read(ModuleActivationStatus.LoadedAssemblyNames());

    /// <summary>Testable form: the caller supplies what counts as loaded.</summary>
    public ModuleActivationReport Read(IReadOnlySet<string> loadedAssemblyNames)
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

        return new ModuleActivationReport(
            ModuleActivationStatus.NotYetLoaded(activation, loadedAssemblyNames));
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
    public bool IsPendingForPackage(string? packagePath)
    {
        var report = Read();
        return !report.IsUndetermined
            && ModuleActivationStatus.IsPendingForPackage(report.Pending, packagePath);
    }
}
