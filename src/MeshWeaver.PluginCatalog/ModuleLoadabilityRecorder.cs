using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The boot that falls back writes the marker the reconcile re-examines (#3650): once the loader
/// has run, this reads the <see cref="FallbackModule"/> and <see cref="IncompatibleModule"/>
/// records it registered and writes each measured head generation's verdict onto that module's
/// sidecar marker (<see cref="ModuleActivationBoot.RecordMeasuredLoadability"/>) — unloadable
/// ⇒ marker written, loaded ⇒ marker cleared.
///
/// <para><b>Why here, and why a hosted service.</b> The loader's records exist only after
/// <c>MeshBuilder.InstallModules</c> ran and the container is built, which is after the point in
/// boot that knows which entries the loader was handed — the same seam
/// <c>ModuleSetAdoptionService</c> sits in, for the same reason. The entries travel in from
/// <c>ConfigureMemexMesh</c> (the union AFTER the mesh-set projection: their <c>Directory</c> is
/// what the loader tried), so nothing is re-derived here; the records come off the container at
/// start. Runs inline in <see cref="StartAsync"/>: a handful of tiny per-module files beside the
/// entries, and the write is what makes the next reconcile honest, so it is not deferred.</para>
///
/// <para>Deliberately NOT gated on the bake (<c>MeshPublicationGate</c>) like the adoption write:
/// the adoption claims "this replica serves this set", which a refused bake falsifies; the marker
/// claims "these bytes do not load on this platform build", which the bake's verdict cannot
/// change. Best-effort: a marker that cannot be written is logged and the next boot measures
/// again.</para>
/// </summary>
public sealed class ModuleLoadabilityRecorder(
    string baseDirectory,
    IReadOnlyList<ModuleActivationEntry> tried,
    IServiceProvider services,
    ILogger<ModuleLoadabilityRecorder>? logger = null) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var transitions = ModuleActivationBoot.RecordMeasuredLoadability(
                baseDirectory,
                tried,
                services.GetServices<FallbackModule>(),
                services.GetServices<IncompatibleModule>(),
                message => logger?.LogInformation("[ModuleLoadability] {Message}", message));
            if (transitions.Count > 0)
                logger?.LogInformation(
                    "[ModuleLoadability] {Count} module marker(s) changed this boot: {Unloadable} now "
                    + "recorded unloadable, {Loaded} cleared.",
                    transitions.Count, transitions.Count(t => t.Unloadable), transitions.Count(t => !t.Unloadable));
        }
        catch (Exception ex)
        {
            // Nothing here may fail a boot: the marker is what makes the next reconcile precise,
            // and a reconcile without it compares identities as it always did.
            logger?.LogWarning(ex,
                "[ModuleLoadability] could not record the boot's module measurements; the next boot "
                + "measures again.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registration for <see cref="ModuleLoadabilityRecorder"/>.</summary>
public static class ModuleLoadabilityRecorderRegistration
{
    /// <summary>
    /// Registers the recorder for the entries the loader is about to be handed — call it beside
    /// <c>AddModuleSetAdoption</c>, with the union AFTER the mesh-set projection, so the
    /// generations it measures are the ones the loader tried.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <param name="baseDirectory">The module root the sidecar lives under.</param>
    /// <param name="tried">The store entries handed to the loader, as projected.</param>
    public static IServiceCollection AddModuleLoadabilityRecord(
        this IServiceCollection services, string baseDirectory, IReadOnlyList<ModuleActivationEntry> tried)
    {
        ArgumentNullException.ThrowIfNull(tried);
        var snapshot = tried.ToImmutableList();
        return services.AddSingleton<IHostedService>(sp => new ModuleLoadabilityRecorder(
            baseDirectory, snapshot, sp, sp.GetService<ILogger<ModuleLoadabilityRecorder>>()));
    }
}
