using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// WHO may bring a package onto this instance — the free / commercial boundary (#830).
///
/// <para><b>The rule.</b> A <b>free</b> package (<see cref="PackageManifest.Price"/> null or 0)
/// syncs, installs and auto-updates with NO special permission: that is what makes the platform
/// baseline and every gratis plugin land on a fresh instance without an operator in the loop. A
/// <b>commercial</b> package (a non-zero price — positive = purchasable, negative = coupon-only;
/// the same "priced" test <see cref="PackageInstaller.EnsureDeclaredAccess"/> already applies when
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
    /// True when the package is COMMERCIAL — it carries a non-zero
    /// <see cref="PackageManifest.Price"/> (positive = purchasable, negative = coupon-only).
    /// Free is the complement: no price at all, or an explicit 0.
    /// </summary>
    /// <param name="manifest">The package manifest (null counts as free — nothing to gate).</param>
    /// <returns><c>true</c> for a priced package.</returns>
    public static bool IsCommercial(this PackageManifest? manifest) =>
        manifest?.Price is { } price && price != 0m;

    /// <summary>
    /// Authorizes installing / auto-updating <paramref name="manifest"/> on this instance. Emits
    /// once and completes when the action may proceed; faults with a
    /// <see cref="PackageAuthorizationException"/> when it may not.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package being installed or updated.</param>
    /// <param name="authorizingUserId">The principal that authorized the action — the clicking user
    /// for a catalog install, the install record's <see cref="PackageManifest.AuthorizedBy"/> for an
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

    private static IObservable<Unit> Refuse(
        PackageManifest manifest, string? userId, ILogger? logger, Exception? cause = null) =>
        Observable.Defer(() =>
        {
            var reason = Reason(manifest, userId);
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
    public static string Reason(PackageManifest manifest, string? userId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var price = manifest.Currency is { Length: > 0 } currency
            ? $"{manifest.Price} {currency}"
            : $"{manifest.Price}";
        var who = string.IsNullOrWhiteSpace(userId)
            ? "no authorizing principal (unattended install)"
            : $"'{userId}'";
        return $"Package '{manifest.Id}' is commercial (price {price}) — installing or auto-updating "
               + $"it requires Global Admin on this instance, and {who} is not one. "
               + "Free packages (no price, or price 0) need no special permission.";
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
