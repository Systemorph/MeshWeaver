using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// 🚨 <b>A NodeType never ERRORS because of delivery</b> (MeshWeaver#3583, measured on
/// memex.systemorph.com on 2026-09-09).
///
/// <para>What happened: Plugins#1555 — a one-line CSS change to <c>Essentials/Email</c> — was
/// synced into the portal 35 minutes after merging, while the only bundle for the portal's
/// framework identity had been baked from the OLD sources. The #2813 fingerprint gate refused the
/// adopted build (right: the bytes were older), the untracked-module gate refused to compile the
/// live source (right: nothing syncs that partition's files), and between them the type settled at
/// <c>compilationStatus: Error</c> / <c>buildProvenance: AdoptionRefused</c> while a perfectly
/// working assembly sat on the record. Every page of the type rendered <i>"This page can't be
/// displayed … the platform refused to run it"</i> for the afternoon — a client mail that was being
/// prepared for sending became unopenable with no change to the record itself.</para>
///
/// <para>The portal owner's rule, which this class is the ONE place for: when the bundle for the
/// running identity is missing, declined or corrupt AND the mesh will not compile the source, the
/// portal keeps serving the LAST build it already holds, with an honest status — the adoption
/// decision is a MODULE-VERSION COMPATIBILITY check (<see cref="ModuleVersionCompatibility"/>), not a
/// fingerprint match. Same MAJOR ⇒ the build keeps serving as
/// <see cref="BuildProvenance.StaleAdopted"/>; a MAJOR bump ⇒ the build is refused
/// (<see cref="BuildProvenance.AdoptionRefused"/>) but the type reports "incompatible, awaiting
/// bundle" rather than a dead page. The fingerprint stays the signal that the source MOVED: it
/// drives the pending status and the readiness notification, never a refusal.</para>
///
/// <para>Pure decisions live here so both compile-watcher gates (RequirePrebuilt and
/// untracked-module) and the owner's adoption judgement settle the SAME record shape, and so every
/// row is unit-testable with no mesh.</para>
/// </summary>
public static class BuildDeliveryHold
{
    /// <summary>
    /// The record a compile-watcher DELIVERY gate settles a Pending type with when it will not
    /// compile it (no bundle for this identity, or the bundle was declined), given whether the
    /// record still names a build usable on this framework.
    ///
    /// <list type="table">
    ///   <item><term>usable build, not refused</term><description><c>Ok</c> +
    ///     <see cref="BuildProvenance.StaleAdopted"/>: the last build keeps serving; the page names
    ///     both versions and fingerprints and says a bundle is awaited; the error text is
    ///     cleared — an Ok record carries none.</description></item>
    ///   <item><term>refused (a MAJOR bump)</term><description><c>Unavailable</c> +
    ///     <see cref="BuildProvenance.AdoptionRefused"/>: the bytes are not run (the execute-time
    ///     gate refuses them), nothing is known to be wrong with the source, a bundle is awaited.
    ///     Not <c>Error</c>: an Error is a Roslyn verdict on the code, and the readiness gate reads
    ///     it as a regression that freezes self-update.</description></item>
    ///   <item><term>no usable build at all</term><description><c>null</c> — nothing to serve; the
    ///     caller parks with the named refusal exactly as before (an honest Error: there is no
    ///     build).</description></item>
    /// </list>
    /// </summary>
    /// <param name="pending">The Pending record the gate observed.</param>
    /// <param name="hasUsableBuild">Whether the record names a build usable on this framework
    /// (<c>NodeTypeCompilationHelpers.HasUsableBuild</c> with the watcher's guards).</param>
    /// <param name="reason">The gate's named reason (what is missing and how to fix it).</param>
    public static NodeTypeDefinition? Settle(NodeTypeDefinition pending, bool hasUsableBuild, string reason)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.BuildProvenance is BuildProvenance.AdoptionRefused)
            return pending with
            {
                DispatchedBuildInputs = null,
                CompilationStatus = CompilationStatus.Unavailable,
                CompilationError = IncompatibleNotice(pending, reason),
                CompiledSources = null,
                RequestedReleaseForce = false,
            };
        if (!hasUsableBuild)
            return null;
        return pending with
        {
            DispatchedBuildInputs = null,
            CompilationStatus = CompilationStatus.Ok,
            // An Ok record carries no error text: the page derives the stale-but-serving sentence
            // from the provenance, the two versions and the two fingerprints (ServingNotice is
            // the log/notification wording of the same facts).
            CompilationError = null,
            CompilationDiagnostics = null,
            // The build IS behind the source — IsDirty stays true, honestly. The next release
            // request re-runs the adoption pass (cheap, no Roslyn) and lands back here until a
            // bundle for this identity catches up.
            CompiledSources = null,
            BuildProvenance = BuildProvenance.StaleAdopted,
            RequestedReleaseForce = false,
        };
    }

    /// <summary>Whether <paramref name="def"/> is in a delivery hold of either kind — the page and
    /// the notifier ask this, so the two cannot disagree about what "held" means.</summary>
    public static bool IsHeld(NodeTypeDefinition? def)
        => def?.BuildProvenance is BuildProvenance.StaleAdopted or BuildProvenance.AdoptionRefused;

    /// <summary>The stale-but-serving sentence: which build serves, which source is current, what
    /// is awaited. English — an operator/log surface; the page localizes its own copy from the
    /// same fields.</summary>
    public static string ServingNotice(NodeTypeDefinition def, string? gateReason = null)
        => $"Serving the last build this mesh holds (module version "
           + $"{ModuleVersionCompatibility.Display(def.AdoptedModuleVersion)}, source fingerprint "
           + $"{Short(def.AdoptedSourceFingerprint)}); the current source (module version "
           + $"{ModuleVersionCompatibility.Display(def.CurrentModuleVersion)}, fingerprint "
           + $"{Short(def.CurrentSourceFingerprint)}) is waiting for a bundle for framework "
           + $"{NodeTypeCompilationHelpers.FrameworkVersion}. Compatible by module version (same "
           + "MAJOR), so the build keeps serving (MeshWeaver#3583)."
           + (string.IsNullOrEmpty(gateReason) ? "" : $" Gate: {gateReason}");

    /// <summary>The incompatible-awaiting-bundle sentence for a refused build.</summary>
    public static string IncompatibleNotice(NodeTypeDefinition def, string? gateReason = null)
        => $"Incompatible build, awaiting bundle: the adopted build is module version "
           + $"{ModuleVersionCompatibility.Display(def.AdoptedModuleVersion)} (source fingerprint "
           + $"{Short(def.AdoptedSourceFingerprint)}) and the current source is module version "
           + $"{ModuleVersionCompatibility.Display(def.CurrentModuleVersion)} (fingerprint "
           + $"{Short(def.CurrentSourceFingerprint)}) — a MAJOR bump, so those bytes are not run. "
           + $"Nothing is known to be wrong with the source; a bundle for framework "
           + $"{NodeTypeCompilationHelpers.FrameworkVersion} is awaited (MeshWeaver#3583)."
           + (string.IsNullOrEmpty(gateReason) ? "" : $" Gate: {gateReason}");

    /// <summary>
    /// Whether the transition <paramref name="before"/> → <paramref name="after"/> is one a person
    /// should hear about (requirement 3 of MeshWeaver#3583): entering a hold, or leaving one
    /// because a build for the current source was adopted or compiled. Pure, so the three
    /// writers that can make the transition emit exactly once each, on the transition itself.
    /// </summary>
    public static DeliveryEvent? EventOf(NodeTypeDefinition? before, NodeTypeDefinition after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var wasHeld = IsHeld(before);
        var isHeld = IsHeld(after);
        if (!wasHeld && isHeld)
            return after.BuildProvenance is BuildProvenance.AdoptionRefused
                ? DeliveryEvent.HeldIncompatible
                : DeliveryEvent.HeldStale;
        if (wasHeld && !isHeld
            && after.BuildProvenance is BuildProvenance.AdoptedVerified or BuildProvenance.Compiled
            && after.CompilationStatus is CompilationStatus.Ok)
            return after.BuildProvenance is BuildProvenance.AdoptedVerified
                ? DeliveryEvent.Adopted
                : DeliveryEvent.Compiled;
        return null;
    }

    /// <summary>What happened to a held type — the readiness signal's vocabulary.</summary>
    public enum DeliveryEvent
    {
        /// <summary>The source moved past the serving build; the build keeps serving.</summary>
        HeldStale = 1,

        /// <summary>The source moved past the serving build by a MAJOR; the build is refused.</summary>
        HeldIncompatible = 2,

        /// <summary>A bundle for the current source was adopted for this identity — the hold is
        /// lifted. "Essentials 1.2.3 adopted."</summary>
        Adopted = 3,

        /// <summary>The current source was compiled locally — the hold is lifted.</summary>
        Compiled = 4,
    }

    /// <summary>
    /// Emits the notification for <paramref name="evt"/> on <paramref name="nodeTypePath"/> — to
    /// the user who requested the release when there is one, else to the platform operators'
    /// bell (a null recipient is how <c>NotificationService.Dispatch</c> addresses them). Cold
    /// delivery subscribed here with explicit error handling: a bell that cannot be written is
    /// logged, never thrown back onto the compile path. Delivery is inverted through
    /// <see cref="ICompileFailureNotifier"/> (the graph/compiler split) — a generic System
    /// notification; the name is the seam's, the content is this event's.
    /// </summary>
    public static void Notify(
        IMessageHub hub, string nodeTypePath, NodeTypeDefinition after, DeliveryEvent evt, ILogger? logger)
    {
        var notifier = hub.ServiceProvider.GetService<ICompileFailureNotifier>();
        if (notifier is null)
        {
            logger?.LogDebug(
                "No ICompileFailureNotifier registered; skipping delivery notification {Event} for {NodeTypePath}.",
                evt, nodeTypePath);
            return;
        }
        var recipient = after.RequestedReleaseBy is { Length: > 0 } by && by != WellKnownUsers.System
            ? by
            : null;
        var typeName = nodeTypePath.Contains('/')
            ? nodeTypePath[(nodeTypePath.LastIndexOf('/') + 1)..]
            : nodeTypePath;
        var package = NodeTypeCompilationHelpers.PartitionOf(nodeTypePath);
        var (title, message) = evt switch
        {
            DeliveryEvent.Adopted => (
                $"{package} {ModuleVersionCompatibility.Display(after.AdoptedModuleVersion)} adopted: '{typeName}'",
                $"A build of '{nodeTypePath}' for the current source (module version "
                + $"{ModuleVersionCompatibility.Display(after.AdoptedModuleVersion)}, fingerprint "
                + $"{Short(after.CurrentSourceFingerprint)}) was adopted for framework "
                + $"{NodeTypeCompilationHelpers.FrameworkVersion}. The type is current again."),
            DeliveryEvent.Compiled => (
                $"'{typeName}' compiled from the current source",
                $"'{nodeTypePath}' was compiled from the current source (fingerprint "
                + $"{Short(after.CurrentSourceFingerprint)}); the build it was serving from is retired."),
            DeliveryEvent.HeldIncompatible => (
                $"'{typeName}' is awaiting a bundle (incompatible build)",
                IncompatibleNotice(after)),
            _ => (
                $"'{typeName}' is serving its last build; source moved ahead",
                ServingNotice(after)),
        };
        notifier.NotifyCompileFailed(
                hub, recipient: recipient, mainNodePath: nodeTypePath, title: title, message: message,
                targetNodePath: nodeTypePath)
            .Subscribe(
                _ => logger?.LogInformation(
                    "Emitted delivery notification {Event} for {NodeTypePath} (recipient {Recipient})",
                    evt, nodeTypePath, recipient ?? "platform admins"),
                ex => logger?.LogWarning(ex,
                    "Failed to emit delivery notification {Event} for {NodeTypePath}", evt, nodeTypePath));
    }

    private static string Short(string? fingerprint)
        => fingerprint is { Length: > 12 } f ? f[..12] : fingerprint ?? "(none)";
}
