using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Records this replica's adoption of the mesh's module set — <b>once this process has been
/// admitted to the mesh</b>. Issue #3478.
///
/// <para><b>Why it moved out of boot.</b> <c>ModuleSetStore.RecordAdoption</c> writes the durable
/// claim <i>"a replica is serving set N"</i>: it is what turns a PROPOSED set into the mesh's
/// CURRENT one, closes <c>ModuleSetIndex.ConvergencePending</c>, and lets the modules GC prune the
/// set records below it. Boot used to write it inline, straight after
/// <c>MeshBuilder.InstallAssemblies</c>, on the reasoning that the claim is <i>"this set is
/// running"</i> rather than <i>"this set was read"</i> — a boot that dies before the install must
/// not close a convergence window it never entered.</para>
///
/// <para>🚨 <b>That reasoning has one more step, and #3478 is what it costs to skip it.</b> A
/// process whose own validation REFUSES it never serves anything either, so it must not make the
/// claim any more than a boot that died. On <c>memex.systemorph.com</c>, 2026-09-06, a pod the bake
/// readiness gate correctly refused ran for two hours having announced itself as a serving replica.
/// So the adoption is now OFFERED to <see cref="MeshPublicationGate"/> instead of written: held
/// while the validation is still forming, written when it passes, and never written when it does
/// not.</para>
///
/// <para><b>Unchanged where nothing is armed.</b> With no <see cref="IMeshAdmissionAuthority"/>
/// registered — every deployment that has not switched the bake readiness gate on, which is the
/// chart default — the gate is <see cref="MeshAdmission.Unarmed"/> and the write runs inline on
/// this <c>StartAsync</c>, a few milliseconds later in boot than before and on the same thread.</para>
/// </summary>
public sealed class ModuleSetAdoptionService : IHostedService, IDisposable
{
    private readonly Func<Unit> record;
    private readonly string describe;
    private readonly MeshPublicationGate? gate;
    private readonly ILogger? logger;
    private IDisposable? subscription;

    /// <summary>
    /// Registers the deferred adoption. <paramref name="record"/> is the side effect itself — a
    /// closure over <c>ModuleSetStore.RecordAdoption</c>'s arguments, so this service carries no
    /// knowledge of the record's shape and boot keeps deciding WHAT is adopted.
    /// </summary>
    /// <param name="describe">What is being adopted, for the gate's log lines.</param>
    /// <param name="record">The (synchronous, best-effort, create-if-absent) adoption write.</param>
    /// <param name="gate">The mesh-admission gate; null on a host that has none.</param>
    /// <param name="logger">Diagnostics.</param>
    public ModuleSetAdoptionService(
        string describe,
        Func<Unit> record,
        MeshPublicationGate? gate,
        ILogger<ModuleSetAdoptionService>? logger = null)
    {
        this.describe = describe;
        this.record = record ?? throw new ArgumentNullException(nameof(record));
        this.gate = gate;
        this.logger = logger;
    }

    /// <summary>
    /// Offers the adoption. Cold by construction: <see cref="MeshPublicationGate.Publish"/> invokes
    /// the factory only when this process may publish, so a refused process never touches the
    /// volume at all.
    ///
    /// <para>Deliberately here and not on <c>ApplicationStarted</c> (where the generations GC had to
    /// move, #2684): this is one <c>File.Exists</c> plus one small temp-and-rename, the exact write
    /// boot already performed synchronously at this point in startup, and holding it back would
    /// delay the mesh-level convergence signal for the common unarmed deployment with nothing to
    /// show for it. On an ARMED one nothing is written here anyway — the gate holds it.</para>
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        IObservable<Unit> Write() => Observable.Defer(() =>
        {
            logger?.LogInformation(
                "ModuleSetAdoption: recording that this replica is serving {Set}", describe);
            return Observable.Return(record());
        });

        var publication = gate is null
            ? Observable.Defer(Write)
            : gate.Publish($"module-set adoption ({describe})", Write);

        subscription = publication.Subscribe(
            _ => { },
            ex => logger?.LogWarning(ex,
                "ModuleSetAdoption: could not record adoption of {Set} — this process still runs "
                + "that set; only the mesh-level 'a replica is serving it' signal is missing",
                describe));
        return Task.CompletedTask;
    }

    /// <summary>Nothing to stop — the write is a one-shot.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        subscription?.Dispose();
        subscription = null;
    }
}

/// <summary>Registration helper for <see cref="ModuleSetAdoptionService"/>.</summary>
public static class ModuleSetAdoptionServiceExtensions
{
    /// <summary>
    /// Registers the deferred module-set adoption. The write is performed by the hosted service
    /// through the mesh-admission gate, never inline at boot — see
    /// <see cref="ModuleSetAdoptionService"/> for why.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <param name="describe">What is being adopted, for the log lines.</param>
    /// <param name="record">The adoption write itself.</param>
    public static IServiceCollection AddModuleSetAdoption(
        this IServiceCollection services, string describe, Action record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return services.AddModuleSetAdoption(describe, _ => record());
    }

    /// <summary>
    /// As <see cref="AddModuleSetAdoption(IServiceCollection, string, Action)"/>, handing the write
    /// the mesh's service provider — so it can record what the loader ACTUALLY installed (#3649:
    /// the <see cref="MeshWeaver.Mesh.FallbackModule"/> records, registered by
    /// <c>MeshBuilder.InstallModules</c> and unknowable at the point boot registers this service).
    /// An overload, not a changed parameter: the <see cref="Action"/> form is binary API.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    /// <param name="describe">What is being adopted, for the log lines.</param>
    /// <param name="record">The adoption write itself, given the built container.</param>
    public static IServiceCollection AddModuleSetAdoption(
        this IServiceCollection services, string describe, Action<IServiceProvider> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return services.AddSingleton<IHostedService>(sp => new ModuleSetAdoptionService(
            describe,
            () =>
            {
                record(sp);
                return Unit.Default;
            },
            sp.GetService<MeshPublicationGate>(),
            sp.GetService<ILogger<ModuleSetAdoptionService>>()));
    }
}
