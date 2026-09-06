using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🚨 <b>The deployment gate (#1754): may this environment be rolled to that release?</b>
///
/// <para>An image is not rollable just because its version is newer. Every package the environment
/// DEPLOYS must also have a usable artifact for the target release — a sealed content bake under
/// the target's framework identity, or a compiled module whose <c>MinMeshVersion</c> floor the
/// target satisfies. Rolling without that check is what turns a routine update into a boot that
/// Roslyn-compiles the whole content set, and a type that fails to compile parks its hub for the
/// full 60 s activation budget.</para>
///
/// <para><b>Why the service lives here, in the registry, and not in each portal's poller.</b> An
/// instance only knows itself; it cannot answer "is this release safe for environment X". Memex
/// holds the environment → instance → installed-package mapping and mounts the same published
/// bundle root every bake lane writes to, so it can answer for any environment. The verdict is
/// then readable by all three paths that roll a version — the self-update poll, CD's own
/// post-promote assertion, and a manual <c>kubectl set image</c> (through
/// <c>/api/plugins/is-updatable</c>) — because a gate only one path honours is not a gate.</para>
///
/// <para>🚨 <b>The consequence, designed for deliberately:</b> this makes the registry a dependency
/// of the environment's ability to update. When the catalogue cannot be read the answer is
/// <see cref="PackageAvailabilityKind.Indeterminate"/> — a HOLD, never a pass — and it is reported
/// as an availability failure with its own reason, never dressed up as a compatibility verdict.
/// The one applicability exemption is stated out loud by
/// <see cref="UpdatabilityVerdict.NotEnforced"/>: a deployment that consumes no CI bakes at all is
/// already compiling at every boot, so holding it could only freeze it forever.</para>
///
/// <para><b>What "deployed" means today</b> is the install records in the <c>Plugins</c> partition —
/// the same records the catalog and the bundle index read. Once #1735 lands, the per-environment
/// composition declares what an environment is SUPPOSED to have, which is strictly better to gate
/// on; this service's <see cref="RequiredPackages"/> is the one place that would change.</para>
/// </summary>
/// <remarks>
/// Not sealed, and <see cref="IsUpdatable"/> is virtual: it is the documented injection seam for the
/// poller's gate, exactly as <c>SelfUpdateHostedService.ReadPolicyStream</c>/<c>RecordAvailable</c>
/// are for its two mesh touches. A test can then pin what the POLLER does with a verdict without
/// also staging an artifact store — the verdict itself is pinned separately, against a real one.
/// </remarks>
public class ReleaseAvailabilityService(
    IMessageHub hub,
    IConfiguration configuration,
    ILogger<ReleaseAvailabilityService>? logger = null)
{
    private readonly IIoPool pool =
        hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem)
        ?? IoPool.Unbounded;

    /// <summary>The published bundle root this deployment mounts, or null when it consumes no CI
    /// bakes.</summary>
    public string? PublishedRoot => configuration[ShippedPrebuiltBundles.PublishedRootConfigKey];

    /// <summary>
    /// 🚨 <b>Does this gate APPLY to this deployment at all?</b> Returns the reason it does not, or
    /// <c>null</c> when it does.
    ///
    /// <para>Decided from CONFIGURATION alone, which is what makes it answerable by a caller that
    /// has no instance of this service — and that is the whole point. "Cannot verify" and "verified
    /// as nothing to verify" are different states, and only the first may hold. A deployment that
    /// consumes no CI bakes already compiles its content at every boot, so a registered gate would
    /// answer <see cref="UpdatabilityVerdict.NotEnforced"/> for it; the gate being ABSENT on such a
    /// deployment tells you nothing new, and holding on it would freeze an environment the gate was
    /// never going to protect.</para>
    ///
    /// <para>Static and shared on purpose: the poller's unwired path (#1754) and
    /// <see cref="IsUpdatable"/> must reach the same applicability answer, and a rule that only one
    /// caller honours is not a rule.</para>
    /// </summary>
    /// <param name="configuration">The host's configuration; null reads as "nothing configured".</param>
    public static string? NotApplicableReason(IConfiguration? configuration) =>
        string.IsNullOrWhiteSpace(configuration?[ShippedPrebuiltBundles.PublishedRootConfigKey])
            ? $"this deployment consumes no CI bakes ({ShippedPrebuiltBundles.PublishedRootConfigKey} "
              + "is not configured), so it already compiles its content at every boot — the "
              + "release-availability gate has nothing to enforce here"
            : null;

    /// <summary>
    /// Is <paramref name="targetVersion"/> a release this environment may be rolled to? Cold —
    /// the file-system leaves run on the <see cref="IoPoolNames.FileSystem"/> pool, never on a hub
    /// action block — and total: every failure resolves to a HOLD carrying its reason, so a
    /// caller can subscribe without a <c>Catch</c> that would turn an incident into a pass.
    /// </summary>
    public virtual IObservable<UpdatabilityVerdict> IsUpdatable(string? targetVersion) =>
        Observable.Defer(() =>
        {
            // ONE applicability rule, shared with the poller's unwired path — see NotApplicableReason.
            if (NotApplicableReason(configuration) is { } notApplicable)
                return Observable.Return(UpdatabilityVerdict.NotEnforced(notApplicable));

            var publishedRoot = PublishedRoot!;

            // 🚨 The DENOMINATOR is read FIRST, and from every identity the root holds except the
            // one under judgement — never from the target's own publication (#3441). See
            // PublishedBundleCatalogue.EverSealedBundles.
            return pool
                .InvokeBlocking(_ => PublishedBundleCatalogue.EverSealedBundles(publishedRoot, logger))
                .SelectMany(floor => !floor.ServesBakes
                    ? Observable.Return(UpdatabilityVerdict.NotEnforced(NoPublicationsReason(publishedRoot)))
                    : RequiredPackages(floor)
                        .SelectMany(required => PublishedBundleCatalogue
                            .Observe(pool, publishedRoot, targetVersion, logger)
                            .Select(observation => ReleaseAvailability.IsUpdatable(
                                observation.Target, required, observation.Artifacts))));
        })
        // 🚨 The gate must ANSWER, always. Its two inputs can each stall indefinitely — a mesh
        // query that never emits its initial snapshot, an I/O pool slot that never frees — and a
        // gate that hangs is strictly worse than one that refuses: the poller's tick never
        // completes, so the update neither applies NOR records a hold, and the environment freezes
        // with nothing anywhere saying why. The timeout converts a stall into the honest answer
        // (Indeterminate ⇒ HOLD, named), which the very next tick re-evaluates from scratch.
        .Timeout(AnswerBudget)
        .Catch((Exception ex) => Observable.Return(ReleaseAvailability.IsUpdatable(
            new ReleaseTarget(targetVersion, null),
            [],
            ReleaseArtifacts.Unreadable(ex is TimeoutException
                ? $"the availability check did not answer within {AnswerBudget.TotalSeconds:0}s"
                : ex.Message))));

    /// <summary>
    /// How long the whole verdict may take. Generous — it bounds a stall, it is not a performance
    /// budget: the reads behind it are a mesh query and a handful of directory stats, so anything
    /// approaching this is wedged rather than slow. Deliberately shorter than the poll interval, so
    /// a stalled tick can never overlap the next one.
    /// </summary>
    private static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Why the gate does not apply to a root that holds no sealed publication under ANY identity.
    ///
    /// <para>🚨 This case used to be a silent PASS, and it was the worst one. With no publication
    /// under the live identity every installed package was judged non-content-bearing, so the
    /// content half of the gate checked NOTHING and still reported updatable — a green tick over
    /// zero evidence. It is stated here instead: an instance whose root serves no bakes already
    /// compiles its content at every boot, exactly like one with no root configured, so the honest
    /// answer is the same <see cref="UpdatabilityVerdict.NotEnforced"/> — <b>updatable with a
    /// reason the caller logs</b>, never a verdict that pretends to have looked.</para>
    /// </summary>
    private static string NoPublicationsReason(string publishedRoot) =>
        $"the published bundle root '{publishedRoot}' holds no sealed publication under any "
        + "framework identity, so this deployment adopts nothing today and already compiles its "
        + "content at every boot — the release-availability gate has nothing to enforce here. "
        + "(If that is unexpected, the bake lanes are not reaching this environment's storage: "
        + "check BAKE_PUBLISH_TARGETS and the publish-bake runs.)";

    /// <summary>
    /// What this environment deploys, as the gate's inputs.
    ///
    /// <para>🚨 <b>Content is required by EVIDENCE, and the evidence may not come from the artifact
    /// under judgement (#3441).</b> A package counts as content-bearing when it has EVER been
    /// sealed under any framework identity this root holds
    /// (<see cref="PublishedBundleCatalogue.EverSealedBundles(string?, Microsoft.Extensions.Logging.ILogger?)"/>)
    /// — not, as before, when it happens to be sealed under the identity running right now.</para>
    ///
    /// <para>The old reading made the denominator a function of the very thing that breaks: a
    /// package whose bake stopped being produced silently left the set of packages the gate asks
    /// about, so every subsequent roll was green about precisely the package that had regressed —
    /// <i>"we kept rolling without edu being properly baked"</i>. Reading it monotonically makes
    /// the gate say what it is for: <b>what this environment has ever been able to adopt, it must
    /// still be able to adopt</b>.</para>
    ///
    /// <para>The exemption that made the old reading attractive is preserved exactly, and for the
    /// same reason: a module-only or NodeType-less package produces no bundle ever, has therefore
    /// never been sealed under any identity, and is still not demanded — demanding one would hold
    /// that environment forever, and an environment silently frozen for weeks is its own
    /// outage.</para>
    /// </summary>
    private IObservable<ImmutableArray<RequiredPackage>> RequiredPackages(SealedBundleFloor floor) =>
        InstalledPackages()
            .Select(installed =>
            {
                var required = installed
                    .Select(manifest => new RequiredPackage(
                        manifest.Id,
                        manifest.Id,
                        LiveFloorOf(manifest.MinMeshVersion),
                        floor.Bundles.Contains(manifest.Id)))
                    .ToImmutableArray();
                // 🚨 PRINT THE DENOMINATOR. A completeness gate whose expected count nobody can
                // read is one nobody can tell from a vacuous one — the count being non-zero is
                // the claim, so it is logged rather than inferred from a pass.
                logger?.LogInformation(
                    "[ReleaseGate] denominator: {ContentBearing} of {Installed} installed package(s) "
                    + "must carry a sealed bake, taken from {Identities} framework identity(ies) this "
                    + "root has published: {Packages}",
                    required.Count(p => p.HasContent),
                    required.Length,
                    floor.Identities,
                    string.Join(", ", required.Where(p => p.HasContent).Select(p => p.Name).Order(StringComparer.Ordinal)));
                return required;
            });

    /// <summary>
    /// A module's floor, but only when the RUNNING platform already satisfies it — otherwise null.
    ///
    /// <para>🚨 The same regression rule the content half uses, for the same reason. SemVer puts
    /// <c>3.0.0-rc4.ci.4049</c> BELOW <c>3.0.0</c>, so a module declaring <c>minMeshVersion:
    /// 3.0.0</c> is below floor on every <c>-rc</c> platform — including the one prod runs. Judged
    /// absolutely it would hold that environment on every release forever; judged as a regression
    /// it holds only a roll that would newly break a module that works today. Since self-update
    /// rolls strictly forward (<c>VersionSelect.IsNewer</c> has already passed), a floor met today
    /// is met by the target too — so this fires exactly where it should, on a ROLLBACK below a
    /// module's declared floor.</para>
    /// </summary>
    private static string? LiveFloorOf(string? minMeshVersion) =>
        ModulePlatformFloor.DeclineReason(minMeshVersion) is null ? minMeshVersion : null;

    /// <summary>
    /// This environment's install records — the same query the bundle index serves from, so the
    /// gate and the catalogue can never disagree about what is installed.
    /// </summary>
    private IObservable<ImmutableArray<PackageManifest>> InstalledPackages() =>
        hub.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageInstaller.InstalledPartition} "
                + $"nodeType:{PackageInstaller.PackageNodeType}"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Take(1)
            .Select(change => change.Items
                .Select(node => node.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                .Where(manifest => manifest is { Id.Length: > 0 })
                .Select(manifest => manifest!)
                .ToImmutableArray());
}
