using MeshWeaver.Data;
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
    ///   <item><term>usable build, within the stale-adoption bound, not refused</term><description><c>Ok</c> +
    ///     <see cref="BuildProvenance.StaleAdopted"/>: the last build keeps serving; the page names
    ///     both versions and fingerprints and says a bundle is awaited; the error text is
    ///     cleared — an Ok record carries none. A record previously refused only on the bound
    ///     (versions measurably same-MAJOR) returns here once the bound no longer refuses it (disabled
    ///     or raised).</description></item>
    ///   <item><term>usable same-MAJOR build PAST the stale-adoption bound</term><description>
    ///     <c>Unavailable</c> + <see cref="BuildProvenance.AdoptionRefused"/>, named by
    ///     <see cref="TooFarBehindNotice"/> (Systemorph/Memex#668): same MAJOR is only the first
    ///     gate, the distance in MINOR versions is the second.</description></item>
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
        => Settle(pending, hasUsableBuild, reason, StaleAdoptionBound.DefaultMaxMinorVersionsBehind);

    /// <summary>
    /// <see cref="Settle(NodeTypeDefinition, bool, string)"/> against an explicit stale-adoption
    /// bound — the compile watcher passes the mesh's configured value.
    /// </summary>
    /// <param name="pending">The Pending record the gate observed.</param>
    /// <param name="hasUsableBuild">Whether the record names a build usable on this framework.</param>
    /// <param name="reason">The gate's named reason (what is missing and how to fix it).</param>
    /// <param name="maxMinorVersionsBehind">The stale-adoption bound (Systemorph/Memex#668): a
    /// usable build more than this many MINOR versions behind the current source is settled as
    /// REFUSED (<c>Unavailable</c> + <see cref="BuildProvenance.AdoptionRefused"/>, named by
    /// <see cref="TooFarBehindNotice"/>) rather than kept serving. Negative disables it.</param>
    public static NodeTypeDefinition? Settle(
        NodeTypeDefinition pending,
        bool hasUsableBuild,
        string reason,
        int maxMinorVersionsBehind)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var pastBound = StaleAdoptionBound.Exceeds(
            pending.AdoptedModuleVersion, pending.CurrentModuleVersion, maxMinorVersionsBehind);
        if (hasUsableBuild
            && pending.BuildProvenance is not BuildProvenance.AdoptionRefused
            && pastBound)
            // Memex#668 — the last build is too far behind its source to keep serving: refused,
            // named, not run (the execute-time gate refuses AdoptionRefused), never an Error.
            return pending with
            {
                DispatchedBuildInputs = null,
                CompilationStatus = CompilationStatus.Unavailable,
                CompilationError = TooFarBehindNotice(pending, maxMinorVersionsBehind, reason),
                CompilationDiagnostics = null,
                CompilationImportRefusals = null,
                CompiledSources = null,
                BuildProvenance = BuildProvenance.AdoptionRefused,
                RequestedReleaseForce = false,
            };
        // A refusal stands unless the versions are MEASURABLY same-MAJOR, within the bound, and a
        // usable build is there to serve: a refusal made ONLY on the bound recovers to StaleAdopted
        // (below) once the operator disables or raises the bound, and is never re-written as a
        // MAJOR incompatibility the versions do not have (Memex#668, Copilot review). A refusal
        // whose versions cannot be compared keeps its pre-#668 behaviour.
        if (pending.BuildProvenance is BuildProvenance.AdoptionRefused
            && (!hasUsableBuild
                || pastBound
                || ModuleVersionCompatibility.Classify(pending.AdoptedModuleVersion, pending.CurrentModuleVersion)
                    is not ModuleVersionVerdict.Compatible))
            return pending with
            {
                DispatchedBuildInputs = null,
                CompilationStatus = CompilationStatus.Unavailable,
                // The refusal keeps naming the rule that made it: the bound (Memex#668) or a MAJOR bump.
                CompilationError = pastBound
                    ? TooFarBehindNotice(pending, maxMinorVersionsBehind, reason)
                    : IncompatibleNotice(pending, reason),
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
            // #4469 — the import finding belongs to the FAILURE it explained, and this record is
            // no longer that failure. Cleared wherever CompilationError/CompilationDiagnostics are,
            // so a type that once carried one can never become Ok while retaining it and hand a
            // later gate settle a stale refusal to prepend (Copilot review).
            CompilationImportRefusals = null,
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

    /// <summary>The too-far-behind sentence for a build refused on the stale-adoption bound
    /// (Systemorph/Memex#668): both versions, the distance, the bound, and what lifts it.</summary>
    /// <param name="def">The record whose adopted build was refused.</param>
    /// <param name="maxMinorVersionsBehind">The bound that refused it.</param>
    /// <param name="gateReason">The delivery gate's own reason, when a gate settled it.</param>
    public static string TooFarBehindNotice(
        NodeTypeDefinition def, int maxMinorVersionsBehind, string? gateReason = null)
        => $"Build too far behind its source, not run: the adopted build is module version "
           + $"{ModuleVersionCompatibility.Display(def.AdoptedModuleVersion)} (source fingerprint "
           + $"{Short(def.AdoptedSourceFingerprint)}) and the current source is module version "
           + $"{ModuleVersionCompatibility.Display(def.CurrentModuleVersion)} (fingerprint "
           + $"{Short(def.CurrentSourceFingerprint)}) — "
           + $"{StaleAdoptionBound.DescribeDistance(def.AdoptedModuleVersion, def.CurrentModuleVersion)}, "
           + $"past the stale-adoption {StaleAdoptionBound.DescribeBound(maxMinorVersionsBehind)}. "
           + $"A build of the current source (a successful compile, or a bundle for framework "
           + $"{NodeTypeCompilationHelpers.FrameworkVersion}) lifts it (Systemorph/Memex#668)."
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
    /// <param name="before">The record before the write (null when there was none).</param>
    /// <param name="after">The record the write produced.</param>
    /// <param name="maxMinorVersionsBehind">The stale-adoption bound (Systemorph/Memex#668) the
    /// refusal was judged against: a refusal ON THE BOUND is <see cref="DeliveryEvent.HeldTooFarBehind"/>,
    /// never <see cref="DeliveryEvent.HeldIncompatible"/> — the two name different causes.</param>
    public static DeliveryEvent? EventOf(
        NodeTypeDefinition? before,
        NodeTypeDefinition after,
        int maxMinorVersionsBehind = StaleAdoptionBound.DefaultMaxMinorVersionsBehind)
    {
        ArgumentNullException.ThrowIfNull(after);
        var wasHeld = IsHeld(before);
        var isHeld = IsHeld(after);
        // Entering a hold, OR moving between the two hold kinds: a serving StaleAdopted build that
        // the source outruns past the bound becomes AdoptionRefused and STOPS running — both are
        // "held", but a person must hear that the type stopped serving (and, the other way, that a
        // lifted bound let it serve again) (Memex#668, Copilot review).
        if (isHeld && (!wasHeld || before!.BuildProvenance != after.BuildProvenance))
            return after.BuildProvenance is not BuildProvenance.AdoptionRefused
                ? DeliveryEvent.HeldStale
                : StaleAdoptionBound.Exceeds(
                    after.AdoptedModuleVersion, after.CurrentModuleVersion, maxMinorVersionsBehind)
                    ? DeliveryEvent.HeldTooFarBehind
                    : DeliveryEvent.HeldIncompatible;
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

        /// <summary>The serving build is same-MAJOR but further behind its source than the
        /// stale-adoption bound (Systemorph/Memex#668); the build is refused.</summary>
        HeldTooFarBehind = 5,
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
    /// <param name="hub">The hub whose notifier delivers.</param>
    /// <param name="nodeTypePath">The NodeType the event is about.</param>
    /// <param name="after">The record the transition produced.</param>
    /// <param name="evt">The event (<see cref="EventOf"/>).</param>
    /// <param name="logger">Where a failed delivery is logged.</param>
    /// <param name="maxMinorVersionsBehind">The stale-adoption bound, for a
    /// <see cref="DeliveryEvent.HeldTooFarBehind"/> body when the record carries no persisted notice.</param>
    public static void Notify(
        IMessageHub hub, string nodeTypePath, NodeTypeDefinition after, DeliveryEvent evt, ILogger? logger,
        int maxMinorVersionsBehind = StaleAdoptionBound.DefaultMaxMinorVersionsBehind)
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
        var adopted = ModuleVersionCompatibility.Display(after.AdoptedModuleVersion);
        var fingerprint = Short(after.CurrentSourceFingerprint);
        var framework = NodeTypeCompilationHelpers.FrameworkVersion;
        // 🚨 The HOLD bodies stay VERBATIM. ServingNotice/IncompatibleNotice/TooFarBehindNotice are declared as the
        // operator/log wording of the record's own fields — "the page localizes its own copy from
        // the same fields" — so keying them here would put a second, divergent translation of the
        // same facts in the catalog. Every TITLE is keyed, and so is each body the notifier itself
        // composes.
        (LocalizableText Title, LocalizableText Message) texts = evt switch
        {
            DeliveryEvent.Adopted => (
                LocalizableText.Keyed(
                    $"{package} {adopted} adopted: '{typeName}'",
                    "notification.delivery.adopted.title",
                    ("package", package), ("version", adopted), ("typeName", typeName)),
                LocalizableText.Keyed(
                    $"A build of '{nodeTypePath}' for the current source (module version "
                    + $"{adopted}, fingerprint "
                    + $"{fingerprint}) was adopted for framework "
                    + $"{framework}. The type is current again.",
                    "notification.delivery.adopted.body",
                    ("nodeTypePath", nodeTypePath), ("version", adopted),
                    ("fingerprint", fingerprint), ("framework", framework))),
            DeliveryEvent.Compiled => (
                LocalizableText.Keyed(
                    $"'{typeName}' compiled from the current source",
                    "notification.delivery.compiled.title", ("typeName", typeName)),
                LocalizableText.Keyed(
                    $"'{nodeTypePath}' was compiled from the current source (fingerprint "
                    + $"{fingerprint}); the build it was serving from is retired.",
                    "notification.delivery.compiled.body",
                    ("nodeTypePath", nodeTypePath), ("fingerprint", fingerprint))),
            // Memex#668 — the refusal on the BOUND: the record's own persisted notice (both
            // versions, the distance and the configured bound), never the MAJOR-bump sentence.
            DeliveryEvent.HeldTooFarBehind => (
                LocalizableText.Keyed(
                    $"'{typeName}' is not run: its build is too far behind its source",
                    "notification.delivery.heldTooFarBehind.title", ("typeName", typeName)),
                LocalizableText.Verbatim(after.CompilationError is { Length: > 0 } persisted
                    ? persisted
                    : TooFarBehindNotice(after, maxMinorVersionsBehind))),
            DeliveryEvent.HeldIncompatible => (
                LocalizableText.Keyed(
                    $"'{typeName}' is awaiting a bundle (incompatible build)",
                    "notification.delivery.heldIncompatible.title", ("typeName", typeName)),
                LocalizableText.Verbatim(IncompatibleNotice(after))),
            _ => (
                LocalizableText.Keyed(
                    $"'{typeName}' is serving its last build; source moved ahead",
                    "notification.delivery.heldStale.title", ("typeName", typeName)),
                LocalizableText.Verbatim(ServingNotice(after))),
        };
        var (title, message) = texts;
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
