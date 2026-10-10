using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>A read of a type that is BUILT but not yet REGISTERED here waits for its registration —
/// it does not answer untyped (Systemorph/MeshWeaver.Plugins#2799).</b>
///
/// <para>A dynamic NodeType's content CLR type enters <see cref="IMeshContentTypeRegistry"/> when
/// one of its instances activates on this replica, or when the registration-only pass
/// (<see cref="DynamicContentTypeRegistrar"/>) reaches it — and that pass runs only after the boot's
/// bake barrier. Readers do not wait for either: measured on memex-cloud (pod
/// <c>884964bb7-6gv59</c>, 2026-10-09), 132 of 134 untyped reads happened at 19:48:37.3, half a
/// second before the pre-warmer even started, from boot-time readers (standing watches on
/// <c>Ops/Status/*</c>, a <c>Posts</c> query, the install records) of types whose bytes were all on
/// the replica (<c>alreadyBaked=414</c>, <c>compiled=0</c>). Gating readiness cannot help those:
/// they are this process's own hosted services, not traffic. So the stream cache's read seams ask
/// this service before they hand out untyped content, and wait for the answer.</para>
///
/// <para><b>What it does, per type, at most once per process:</b> read the type's NodeType record
/// (as system — infrastructure, not a user read) and run
/// <see cref="DynamicContentTypeRegistrar.RegisterType"/>: the record's own claim of a usable build,
/// the identity-checked bytes from the assembly store (refetched from the shipped bundle when this
/// replica lacks them), a configuration load from that EXISTING assembly and one transient probe.
/// <b>Never a compile and never a record write</b> — exactly the pass's route, which is why it may
/// run on a read where the enrichment path must not (<c>ContentTypeRegistration</c>'s remarks). A
/// type with no usable build answers <see cref="ContentTypeRegistrationStatus.NotBaked"/> at once
/// and the read degrades as before; compiling it stays the first activation's job.</para>
///
/// <para><b>Why the boot-barrier concern does not apply:</b> the pass waited for the barrier so a
/// probe would never meet an adopted-but-not-yet-loadable file and trip the loader's bad-image
/// delete. Store writes are atomic (temp file + rename, MeshWeaver#1387), and the probe loads only
/// bytes whose MVID IS the record's published build (<c>ServedBuildIdentity</c>); anything else is
/// <see cref="ContentTypeRegistrationStatus.StaleBytes"/> or
/// <see cref="ContentTypeRegistrationStatus.BytesMissing"/> and nothing is loaded.</para>
///
/// <para><b>Bounded.</b> One attempt per type, coalesced across every concurrent reader through an
/// instance <see cref="PromiseCache{TKey,TValue}"/>; the load and the probe run on the
/// <see cref="IoPoolNames.FileSystem"/> pool inside the registrar. A verdict is kept for the
/// process — the other two registration routes (activation, the pass) still run for a type this
/// one could not register — except <see cref="ContentTypeRegistrationStatus.Faulted"/>, which is a
/// failure of this replica rather than a verdict about the type, so the next read asks again. The
/// record read carries the same budget as the pass's enumeration
/// (<see cref="RecordReadBudget"/>): a read must never wait on a store that does not answer, and an
/// expired budget is a named <see cref="ContentTypeRegistrationStatus.Faulted"/> outcome, logged.</para>
///
/// <para>Registered by <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>
/// beside the pass. A host without it keeps the old behaviour: the seams degrade and the late
/// re-type waits for a registration. A mesh-scoped instance singleton, never static.</para>
/// </summary>
public sealed class ContentTypeOnDemandRegistration(ILogger<ContentTypeOnDemandRegistration> logger)
{
    /// <summary>How long the one NodeType-record read may take before the attempt is Faulted.</summary>
    public static readonly TimeSpan RecordReadBudget = TimeSpan.FromSeconds(30);

    private readonly PromiseCache<string, ContentTypeRegistrationOutcome> attempts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The outcome of registering <paramref name="nodeTypePath"/>'s content type on this process —
    /// immediately <see cref="ContentTypeRegistrationStatus.AlreadyRegistered"/> when it already
    /// resolves, otherwise the (shared) attempt. Always terminates with exactly one outcome and never
    /// errors: a fault is a <see cref="ContentTypeRegistrationStatus.Faulted"/> outcome.
    /// </summary>
    /// <param name="mesh">The mesh hub whose registry, store and compilation service are used.</param>
    /// <param name="nodeTypePath">The NodeType whose content type is wanted.</param>
    public IObservable<ContentTypeRegistrationOutcome> EnsureRegistered(IMessageHub mesh, string nodeTypePath)
    {
        var registry = mesh.ServiceProvider.GetService<IMeshContentTypeRegistry>();
        if (registry is not null && registry.TryResolveByNodeType(nodeTypePath, out _))
            return Observable.Return(new ContentTypeRegistrationOutcome(
                nodeTypePath, ContentTypeRegistrationStatus.AlreadyRegistered));

        // The connection is OWNED by the mesh hub whose services the attempt resolves, so the
        // mesh's disposal releases an attempt still in flight.
        var attempt = attempts.GetOrAdd(nodeTypePath, path => Attempt(mesh, path)
            .Replay(1)
            .AutoConnectOwnedBy(mesh, nameof(ContentTypeOnDemandRegistration)));
        return attempt.Do(outcome =>
        {
            // A failure of THIS replica, not a verdict about the type: the next read asks again.
            // Pair-exact, so a fresh attempt another reader already started is never dropped.
            if (outcome.Status is ContentTypeRegistrationStatus.Faulted)
                attempts.Release(nodeTypePath, attempt);
        });
    }

    private IObservable<ContentTypeRegistrationOutcome> Attempt(IMessageHub mesh, string path)
    {
        var meshService = mesh.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(new ContentTypeRegistrationOutcome(
                path, ContentTypeRegistrationStatus.NotBaked, "no mesh service on this mesh"));
        var accessService = mesh.ServiceProvider.GetService<AccessService>();
        var startedAt = DateTimeOffset.UtcNow;
        return accessService.RunAsSystem(
                () => meshService
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
                    .Take(1)
                    .Timeout(RecordReadBudget))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, mesh.JsonSerializerOptions, logger))
            .SelectMany(types => DynamicContentTypeRegistrar.RegisterType(mesh, types, path, logger))
            .Take(1)
            .DefaultIfEmpty(new ContentTypeRegistrationOutcome(
                path, ContentTypeRegistrationStatus.Faulted, "the NodeType record read completed without an answer"))
            .Catch<ContentTypeRegistrationOutcome, Exception>(ex => Observable.Return(
                new ContentTypeRegistrationOutcome(
                    path, ContentTypeRegistrationStatus.Faulted, $"{ex.GetType().Name}: {ex.Message}")))
            .Do(outcome =>
            {
                var elapsed = DateTimeOffset.UtcNow - startedAt;
                if (outcome.Status is ContentTypeRegistrationStatus.Faulted)
                    logger.LogWarning(
                        "ContentTypeOnDemandRegistration: registering {TypePath} for a read FAULTED after {Elapsed} — "
                        + "{Detail}. The read answers untyped; the next read of this type asks again",
                        path, elapsed, outcome.Detail);
                else
                    logger.LogInformation(
                        "ContentTypeOnDemandRegistration: {TypePath} → {Status} in {Elapsed} for a read ({Detail}). "
                        + "Nothing was compiled and no NodeType record was written",
                        path, outcome.Status, elapsed, outcome.Detail ?? "-");
            });
    }
}
