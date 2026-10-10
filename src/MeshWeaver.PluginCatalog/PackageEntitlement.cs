using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHO may bring a package onto this instance — the free / commercial boundary (#830).
///
/// <para><b>The rule.</b> A <b>free</b> package (<see cref="PackageManifest.Price"/> null or 0, and
/// no <see cref="PackageManifest.ContactEmail"/>) syncs, installs and auto-updates with NO special
/// permission: that is what makes the platform baseline and every gratis plugin land on a fresh
/// instance without an operator in the loop. A <b>commercial</b> package (a non-zero price —
/// positive = purchasable, negative = coupon-only — or a named sales contact; the same
/// <see cref="IsCommercial"/> test <see cref="PackageInstaller.EnsureDeclaredAccess"/> applies when
/// it decides a partition installs gated) requires <b>Global Admin</b> on the installing instance
/// to be accessed, installed or auto-updated.</para>
///
/// <para><b>Where the check belongs.</b> On the ACTION, never only on the surface. Gating just the
/// catalog UI is how the boundary came to be drawn in the wrong place: the tab was admin-only while
/// the machine paths — the unattended default install and <see cref="PluginUpdateWatcher"/>'s
/// auto-update — installed priced packages with no check at all. So the gate sits at the install
/// entry points (<see cref="PackageInstaller.Install"/>,
/// <see cref="PackageInstaller.InstallNodeRepoDelta"/>), which every path funnels through, and the
/// UI gate is only there to keep an unauthorized viewer from clicking a button that would refuse.</para>
///
/// <para><b>The authorizing principal is explicit, and absence fails closed.</b> An install runs
/// under SYSTEM for its whole lifetime (it is provisioning — see
/// <see cref="CatalogLayoutAreas.InstallPackage"/>), so the ambient identity at the moment of the
/// write is System and can authorize nothing. The principal that AUTHORIZED the action is therefore
/// captured before the impersonation and threaded through as
/// <c>authorizingUserId</c>; it is stamped on the install record
/// (<see cref="PackageManifest.AuthorizedBy"/>) so an unattended update can re-verify the same
/// principal is still an admin. No principal (a boot-time default install, a webhook reaction) means
/// nobody authorized it: free packages proceed, commercial ones are refused.</para>
///
/// <para><b>The second authority: a verified governed activity.</b> Installing a package on an
/// instance is decided by a governed <c>package.provision</c> activity signed by people, never by a
/// person's standing rights. So the authorizing
/// principal may also be the activity itself — <c>Governance/Activities/{id}</c>, which the Store's
/// provision control plane hands over once it has verified the request. The gate does not take that
/// on trust: it reads the activity AUTHORITATIVELY from storage
/// (<see cref="MeshExtensions.ReadGovernedActivity"/>, the same identity-independent read the
/// broad-grant guard verifies with, so core takes no dependency on the Governance package) and
/// admits the commercial package only when the activity is a <c>Governance/Activity</c> node that
/// runs the <c>package.provision</c> standard, has started (its signatures consumed — monotone, so
/// an unattended update re-checking the stamped principal later gets the same answer), carries at
/// least one consumed signature, and was signed for THIS package id
/// (<see cref="WhyNotAuthorizingActivity"/>). Anything else — a forged or absent path, an unsigned
/// or unstarted activity, another standard, another package — is refused exactly like a non-admin,
/// with the reason in the sentence. The activity is stamped on the install record as
/// <see cref="PackageManifest.AuthorizedBy"/> and named in the log. This widens WHO may authorize,
/// nothing else: the registry still serves only what the instance's plan tier covers, and the
/// licence and parameter gates still run.</para>
///
/// <para>A refusal is never silent — it logs and it FAULTS the install observable with a
/// <see cref="PackageAuthorizationException"/> carrying the reason, so the caller surfaces it
/// (the catalog logs it; the update watcher turns it into a notification on the install record).</para>
/// </summary>
public static class PackageEntitlement
{
    /// <summary>
    /// How long the gate waits for the authoritative admin answer. The permission evaluator is a
    /// <c>seed.Concat(enriched)</c> chain over the live <c>AccessAssignment</c> query: its FIRST
    /// emission can be a premature <c>false</c> seeded before that query lands, so the gate waits
    /// for a positive confirmation rather than sampling the first value (the same shape every
    /// <c>IsGlobalAdmin</c> gate in the codebase uses). This is a bound on ONE authoritative
    /// answer, not a retry budget — nothing is re-subscribed and nothing is polled.
    /// </summary>
    private static readonly TimeSpan AdminConfirmationWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the gate waits for the authoritative read of a governed activity. One storage read,
    /// not a retry budget; no answer within it is a refusal (fail closed).
    /// </summary>
    private static readonly TimeSpan ActivityReadWindow = TimeSpan.FromSeconds(15);

    /// <summary>Where governed activities live: <c>Governance/Activities/{id}</c>.</summary>
    public const string GovernedActivitiesNamespace = "Governance/Activities";

    /// <summary>The node type the Governance package writes an activity as.</summary>
    public const string GovernedActivityNodeType = "Governance/Activity";

    /// <summary>The ONE governed standard whose activity may authorize installing a package.</summary>
    public const string PackageProvisionStandard = "package.provision";

    /// <summary>The activity input that names the package two people signed for.</summary>
    public const string PackageInput = "package";

    /// <summary>
    /// True when the package is COMMERCIAL — it carries a non-zero
    /// <see cref="PackageManifest.Price"/> (positive = purchasable, negative = coupon-only) OR it
    /// names a sales contact (<see cref="PackageManifest.ContactEmail"/>: sold with a person in the
    /// loop). Free is the complement: nothing to pay and nobody to ask.
    ///
    /// <para>🚨 The contact-sales half is not a nicety. A package sold that way normally names NO
    /// price — "nothing to self-serve" — so on the price-only test it read as FREE, and the two
    /// decisions that call this both took the wrong branch at once: <see cref="Authorize"/> let any
    /// unattended install pull it in with no admin, and
    /// <see cref="PackageInstaller.EnsureDeclaredAccess"/> published the partition
    /// (<c>_Policy · PublicRead = true</c>, since these declare no <c>publicSegments</c>) until the
    /// Store's <c>PluginGate</c> reconciled it dark again. The whole point of naming a sales contact
    /// is that the content is NOT self-service.</para>
    /// </summary>
    /// <param name="manifest">The package manifest (null counts as free — nothing to gate).</param>
    /// <returns><c>true</c> for a priced or contact-sales package.</returns>
    public static bool IsCommercial(this PackageManifest? manifest) =>
        manifest?.Price is { } price && price != 0m
        || !string.IsNullOrWhiteSpace(manifest?.ContactEmail);

    /// <summary>
    /// Authorizes installing / auto-updating <paramref name="manifest"/> on this instance. Emits
    /// once and completes when the action may proceed; faults with a
    /// <see cref="PackageAuthorizationException"/> when it may not.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package being installed or updated.</param>
    /// <param name="authorizingUserId">The principal that authorized the action — the clicking user
    /// for a catalog install, a verified governed activity (<c>Governance/Activities/{id}</c>) for a
    /// governed provision, the install record's <see cref="PackageManifest.AuthorizedBy"/> for an
    /// unattended update, null when nobody authorized it (boot-time provisioning).</param>
    /// <param name="logger">Diagnostics; a refusal is logged here as a warning.</param>
    /// <returns>A cold observable that emits <see cref="Unit"/> on allow and faults on refusal.</returns>
    public static IObservable<Unit> Authorize(
        IMessageHub hub, PackageManifest manifest, string? authorizingUserId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(manifest);

        // Free — the common case, and the whole point of the rule: no permission, no round-trip.
        if (!manifest.IsCommercial())
            return Observable.Return(Unit.Default);

        if (string.IsNullOrWhiteSpace(authorizingUserId))
            return Refuse(manifest, null, logger);

        // A governed activity is verified, never trusted by its path — and never asked whether it
        // is a global admin: it is not a person, and its authority is its signatures.
        if (IsGovernedActivityPrincipal(authorizingUserId))
            return AuthorizeByActivity(hub, manifest, authorizingUserId!.Trim(), logger);

        // Wait for the POSITIVE confirmation, not the first emission (see AdminConfirmationWindow).
        // TakeDecisionOutsideGate leaves the evaluator's Rx gate before the install's real work
        // starts — an install writes nodes and publishes changes, exactly the continuation #899
        // showed must not run inside the permission fold.
        return hub.IsGlobalAdmin(authorizingUserId!)
            .Where(isAdmin => isAdmin)
            .TakeDecisionOutsideGate()
            .Timeout(AdminConfirmationWindow)
            .Select(_ => Unit.Default)
            // No positive within the window IS the refusal (a non-admin's stream simply never
            // emits true). Any other fault is reported as a refusal too — fail closed — but keeps
            // its cause as the inner exception so a broken evaluator never reads as "not an admin".
            .Catch<Unit, Exception>(exception => Refuse(
                manifest, authorizingUserId, logger,
                exception is TimeoutException ? null : exception));
    }

    /// <summary>
    /// Whether <paramref name="principal"/> names a governed activity — exactly
    /// <c>Governance/Activities/{id}</c>, one segment, nothing below it. Pure. Says nothing about
    /// whether the activity exists or authorizes anything; that is
    /// <see cref="WhyNotAuthorizingActivity"/>'s question.
    /// </summary>
    /// <param name="principal">The authorizing principal as handed to the gate.</param>
    /// <returns><c>true</c> for an activity path.</returns>
    public static bool IsGovernedActivityPrincipal(string? principal)
    {
        var trimmed = principal?.Trim();
        const string prefix = GovernedActivitiesNamespace + "/";
        if (trimmed is null || !trimmed.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var id = trimmed[prefix.Length..];
        return id.Length > 0 && !id.Contains('/');
    }

    /// <summary>
    /// Why the governed activity read at <paramref name="activityPath"/> does NOT authorize
    /// installing <paramref name="manifest"/>, or null when it does. The activity must be a
    /// <c>Governance/Activity</c> node at that very path, run the <c>package.provision</c> standard,
    /// have STARTED (<see cref="GovernedActivityFacts.HasStarted"/> — its gates were green and its
    /// signatures consumed; monotone, so the answer does not depend on when it is asked), carry at
    /// least one consumed signature, and have been signed for THIS package id (ordinal). Pure.
    /// </summary>
    /// <param name="facts">The activity as read from storage, or null when nothing readable is there.</param>
    /// <param name="activityPath">The path the principal named.</param>
    /// <param name="manifest">The package being installed or updated.</param>
    /// <returns>The reason it does not authorize, or null.</returns>
    public static string? WhyNotAuthorizingActivity(
        GovernedActivityFacts? facts, string activityPath, PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (facts is null)
            return $"no readable governed activity exists at '{activityPath}'";
        if (!string.Equals(facts.Path, activityPath, StringComparison.Ordinal))
            return $"'{activityPath}' resolved to '{facts.Path}', not to itself";
        if (!string.Equals(facts.NodeType, GovernedActivityNodeType, StringComparison.Ordinal))
            return $"'{activityPath}' is a {facts.NodeType ?? "(untyped)"} node, not a {GovernedActivityNodeType}";
        if (!string.Equals(facts.StandardId, PackageProvisionStandard, StringComparison.Ordinal))
            return $"'{activityPath}' runs {facts.StandardId}, not {PackageProvisionStandard}";
        if (!facts.HasStarted)
            return $"'{activityPath}' is {(facts.State.Length == 0 ? "(no state)" : facts.State)} — it has not started, so its signatures were never consumed";
        if (facts.ConsumedSignatures < 1)
            return $"'{activityPath}' carries no consumed signature";
        var signedFor = facts.Input(PackageInput);
        if (!string.Equals(signedFor, manifest.Id?.Trim(), StringComparison.Ordinal))
            return $"'{activityPath}' was signed for package '{signedFor ?? "(none)"}', not '{manifest.Id}'";
        return null;
    }

    /// <summary>
    /// The governed half of <see cref="Authorize"/>: reads the activity authoritatively and admits
    /// on <see cref="WhyNotAuthorizingActivity"/> == null, logging which activity authorized the
    /// package; refuses otherwise with that reason in the sentence. A read that faults or does not
    /// answer within <see cref="ActivityReadWindow"/> is a refusal that keeps its cause.
    /// </summary>
    private static IObservable<Unit> AuthorizeByActivity(
        IMessageHub hub, PackageManifest manifest, string activityPath, ILogger? logger) =>
        hub.ReadGovernedActivity(activityPath)
            .Take(1)
            .Timeout(ActivityReadWindow)
            .SelectMany(facts =>
            {
                if (WhyNotAuthorizingActivity(facts, activityPath, manifest) is { } why)
                    return Refuse(manifest, activityPath, logger, detail: why);
                logger?.LogInformation(
                    "[PackageEntitlement] commercial package {Package} AUTHORIZED by governed activity {Activity} "
                    + "({Standard}, {State}, {Signatures} consumed signature(s), signed for {SignedFor})",
                    manifest.Id, activityPath, facts!.StandardId, facts.State, facts.ConsumedSignatures,
                    facts.Input(PackageInput));
                return Observable.Return(Unit.Default);
            })
            .Catch<Unit, Exception>(exception => exception is PackageAuthorizationException
                ? Observable.Throw<Unit>(exception)
                : Refuse(manifest, activityPath, logger,
                    exception is TimeoutException ? null : exception,
                    detail: $"the activity at '{activityPath}' could not be read"));

    private static IObservable<Unit> Refuse(
        PackageManifest manifest, string? userId, ILogger? logger, Exception? cause = null, string? detail = null) =>
        Observable.Defer(() =>
        {
            var reason = RefusalReason(manifest, userId, detail);
            if (cause is null)
                logger?.LogWarning("[PackageEntitlement] {Reason}", reason);
            else
                logger?.LogWarning(cause, "[PackageEntitlement] {Reason}", reason);
            return Observable.Throw<Unit>(cause is null
                ? new PackageAuthorizationException(reason)
                : new PackageAuthorizationException(reason, cause));
        });

    /// <summary>
    /// The speaking refusal reason — names the package, its price and the principal that was asked,
    /// so a refused install is diagnosable from one log line instead of looking like a silent skip.
    /// </summary>
    /// <param name="manifest">The refused package.</param>
    /// <param name="userId">The principal that authorized the action, or null when there was none.</param>
    /// <returns>The human-readable reason.</returns>
    public static string Reason(PackageManifest manifest, string? userId) => RefusalReason(manifest, userId, null);

    /// <summary>
    /// <see cref="Reason"/> with the specific <paramref name="detail"/>
    /// of why the principal does not authorize — for a governed activity, which check it failed.
    /// </summary>
    /// <param name="manifest">The refused package.</param>
    /// <param name="userId">The principal that authorized the action, or null when there was none.</param>
    /// <param name="detail">Why that principal does not authorize it, or null.</param>
    /// <returns>The human-readable reason.</returns>
    public static string RefusalReason(PackageManifest manifest, string? userId, string? detail)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Name what made it commercial — a price, or the sales contact. A contact-sales package has
        // no price, and reporting "price " with a blank where the number goes reads as a bug in the
        // gate rather than as the deliberate refusal it is.
        var terms = manifest.Price is { } amount
            ? manifest.Currency is { Length: > 0 } currency
                ? $"price {amount} {currency}"
                : $"price {amount}"
            : $"contact sales: {manifest.ContactEmail}";
        var who = string.IsNullOrWhiteSpace(userId)
            ? "no authorizing principal (unattended install)"
            : $"'{userId}'";
        return $"Package '{manifest.Id}' is commercial ({terms}) — installing or auto-updating "
               + "it requires Global Admin on this instance or a verified governed "
               + $"{PackageProvisionStandard} activity signed for it, and {who} is neither"
               + (string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})")
               + ". Free packages (no price and no sales contact, or price 0) need no special "
               + "permission.";
    }
}

/// <summary>
/// A package action was refused because the package is commercial and the authorizing principal is
/// not a global admin (#830). Distinct from an install FAILURE: nothing was attempted.
/// </summary>
public sealed class PackageAuthorizationException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="PackageAuthorizationException"/> class.</summary>
    public PackageAuthorizationException()
    {
    }

    /// <summary>Initializes a new instance with the refusal <paramref name="message"/>.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    public PackageAuthorizationException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and an inner exception.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PackageAuthorizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
