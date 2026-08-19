using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Enforces <see cref="LicenseContent.RequiresAcceptance"/> on the install path, and records the
/// <see cref="LicenseAcceptance"/> that satisfies it.
///
/// <para><b>Why this exists.</b> <c>LicenseContent</c> and <c>LicenseAcceptance</c> shipped as node
/// types with a catalog, a body, a <c>RequiresAcceptance</c> flag and a body hash — and no callers
/// whatsoever: nothing read the flag and nothing ever wrote an acceptance. A consent surface with no
/// enforcement records nothing and proves nothing.</para>
///
/// <para>It sits BESIDE <see cref="PackageEntitlement"/>, on the action, for the same reason: every
/// install path funnels through <see cref="PackageInstaller"/>, so the machine paths (unattended
/// default install, the update watcher) are gated identically to a click. The two answer different
/// questions and neither substitutes for the other — entitlement is <i>may you</i>, acceptance is
/// <i>have you agreed to the terms</i>.</para>
///
/// <para>🚨 <b>The body hash is the point.</b> An acceptance is only meaningful against the text
/// that was actually shown, so a recorded acceptance whose hash no longer matches the licence body
/// does NOT satisfy the gate — revised terms need fresh consent. Storing the hash without checking
/// it would be a consent record that quietly covers terms the user never read.</para>
/// </summary>
public static class LicenseAcceptanceGate
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// SHA-256 hex of a licence body, over its NORMALIZED text — line endings unified and trailing
    /// whitespace trimmed. Normalizing matters because a body that round-trips through git or an
    /// editor can change bytes without changing terms, and a hash that flips on a CRLF would revoke
    /// every recorded acceptance for no reason a reader could see.
    /// </summary>
    /// <param name="body">The licence text.</param>
    /// <returns>Lowercase hex digest.</returns>
    public static string BodyHash(string? body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(body)))).ToLowerInvariant();

    private static string Normalize(string? body) =>
        (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>
    /// Gates the install of <paramref name="manifest"/> on a recorded, current acceptance. Emits
    /// once and completes when the install may proceed; faults with a
    /// <see cref="LicenseAcceptanceRequiredException"/> when consent is missing or stale.
    /// </summary>
    /// <param name="hub">The installing hub.</param>
    /// <param name="manifest">The package being installed.</param>
    /// <param name="acceptingUserId">The principal whose acceptance counts — the same principal that
    /// authorized the action. Null (boot-time provisioning) can only satisfy a licence that asks for
    /// nothing.</param>
    /// <param name="logger">Diagnostics; a refusal logs a warning.</param>
    /// <returns>A cold observable that emits on allow and faults on refusal.</returns>
    public static IObservable<Unit> Require(
        IMessageHub hub, PackageManifest manifest, string? acceptingUserId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(manifest);

        var spdxId = manifest.License;
        // Unspecified terms: nothing to accept, and inventing a licence to demand consent to would
        // assert a grant nobody made (the rule Package.License already follows).
        if (string.IsNullOrWhiteSpace(spdxId))
            return Observable.Return(Unit.Default);

        // 🚨 RunAsSystem, never Observable.Using(access.ImpersonateAsSystem, …) (#1790). Rx runs a
        // Using factory on the SUBSCRIBING thread and disposes on termination, leaving that thread
        // latched as System — and this gate's subscriber is the INSTALLER, which would then run its
        // whole pipeline with Permission.All. The widest cold pipeline goes inside the factory, so
        // the catalog + acceptance reads are System (they must be: an acceptance lives in another
        // user's partition) while every notification reaches the installer as itself.
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => ReadLicense(hub, spdxId!, logger)
            .SelectMany(license =>
            {
                // No terms document in the catalog — including every SPDX EXPRESSION
                // ("Apache-2.0 OR MIT"), which names a choice rather than one node. We cannot show
                // terms we do not hold, so demanding consent to them is impossible rather than
                // strict. Logged, so an unresolved id is visible instead of silent.
                //
                // 🚨 This is NOT an access decision and must never become one: what a caller MAY
                // install is decided by PackageEntitlement and, for an instance, by its sync
                // licence. A licence that genuinely requires acceptance is one we authored and
                // shipped INTO the catalog, so it resolves here.
                if (license is null)
                {
                    logger?.LogInformation(
                        "Package {Package} declares licence {Spdx}, which is not in the catalog — "
                        + "no acceptance is demanded for terms the platform cannot display.",
                        manifest.Id, spdxId);
                    return Observable.Return(Unit.Default);
                }

                if (!license.RequiresAcceptance)
                    return Observable.Return(Unit.Default);

                if (string.IsNullOrWhiteSpace(acceptingUserId))
                    return Refuse(manifest, spdxId!, null,
                        "no principal is present to accept them", logger);

                var expected = BodyHash(license.Body);
                return ReadAcceptance(hub, acceptingUserId!, manifest.Id, logger)
                    .SelectMany(acceptance =>
                    {
                        if (acceptance is null)
                            return Refuse(manifest, spdxId!, acceptingUserId,
                                "no acceptance has been recorded", logger);

                        // Terms revised since consent was given — the whole reason the record
                        // carries a body hash.
                        if (!string.Equals(acceptance.BodyHash, expected, StringComparison.OrdinalIgnoreCase))
                            return Refuse(manifest, spdxId!, acceptingUserId,
                                "the recorded acceptance is against an earlier version of the terms",
                                logger);

                        return Observable.Return(Unit.Default);
                    });
            }));
    }

    /// <summary>
    /// Records <paramref name="acceptingUserId"/>'s acceptance of <paramref name="manifest"/>'s
    /// licence, stamped with the hash of the text as it stands NOW — which is the text the caller
    /// is responsible for having shown.
    ///
    /// <para>The record is written into the accepting user's OWN partition
    /// (<c>{userId}/_LicenseAcceptance/{packageId}</c>): it is evidence the user holds, not a claim
    /// the platform makes about them.</para>
    /// </summary>
    /// <param name="hub">The hub performing the write.</param>
    /// <param name="manifest">The package the licence was accepted for.</param>
    /// <param name="acceptingUserId">Who accepted.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>The stored acceptance, or an empty sequence when the licence asks for none.</returns>
    public static IObservable<LicenseAcceptance> Record(
        IMessageHub hub, PackageManifest manifest, string acceptingUserId, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(acceptingUserId))
            throw new ArgumentException("An acceptance must name who gave it.", nameof(acceptingUserId));
        if (string.IsNullOrWhiteSpace(manifest.License))
            return Observable.Empty<LicenseAcceptance>();

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // Same seal as Require — the write lands in the accepting user's partition, so it runs as
        // System, but the caller that subscribed must not be left holding that identity.
        return accessService.RunAsSystem(() => ReadLicense(hub, manifest.License!, logger)
            .SelectMany(license =>
            {
                if (license is null)
                    return Observable.Empty<LicenseAcceptance>();

                var acceptance = new LicenseAcceptance
                {
                    SpdxId = license.SpdxId,
                    PackageId = manifest.Id,
                    UserId = acceptingUserId,
                    AcceptedAt = DateTimeOffset.UtcNow,
                    BodyHash = BodyHash(license.Body),
                };

                var node = new MeshNode(manifest.Id,
                    $"{acceptingUserId}/{LicenseNodeType.AcceptanceNamespace}")
                {
                    Name = $"Licence accepted: {license.SpdxId} for {manifest.Id}",
                    NodeType = LicenseNodeType.AcceptanceNodeType,
                    State = MeshNodeState.Active,
                    Content = acceptance,
                };

                return meshService.CreateOrUpdateNode(node)
                    .Select(_ =>
                    {
                        logger?.LogInformation(
                            "Licence {Spdx} accepted for {Package} by {User}",
                            license.SpdxId, manifest.Id, acceptingUserId);
                        return acceptance;
                    });
            }));
    }

    private static IObservable<LicenseContent?> ReadLicense(
        IMessageHub hub, string spdxId, ILogger? logger) =>
        Read<LicenseContent>(hub, WellKnownLicenses.PathFor(spdxId), logger);

    private static IObservable<LicenseAcceptance?> ReadAcceptance(
        IMessageHub hub, string userId, string packageId, ILogger? logger) =>
        Read<LicenseAcceptance>(hub, LicenseNodeType.AcceptancePath(userId, packageId), logger);

    /// <summary>
    /// One-shot read by exact path. The System identity is supplied by the caller's
    /// <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/> scope, so this composes INSIDE that
    /// scope rather than opening its own — one seal per public entry point, not one per read.
    ///
    /// <para>A read FAILURE faults the sequence rather than resolving to null: "the mesh was briefly
    /// unreachable" must never read as "no acceptance is required", which is the difference between
    /// failing closed and failing open.</para>
    /// </summary>
    private static IObservable<T?> Read<T>(IMessageHub hub, string path, ILogger? logger)
        where T : class =>
        hub.GetMeshNode(path, ReadTimeout)
            .Take(1)
            .Select(node => node?.ContentAs<T>(hub.JsonSerializerOptions));

    private static IObservable<Unit> Refuse(
        PackageManifest manifest, string spdxId, string? userId, string reason, ILogger? logger) =>
        Observable.Defer(() =>
        {
            var message =
                $"Installing '{manifest.Id}' requires accepting its licence ({spdxId}): {reason}"
                + (userId is null ? "." : $" for '{userId}'.");
            logger?.LogWarning("Licence acceptance refused: {Message}", message);
            return Observable.Throw<Unit>(new LicenseAcceptanceRequiredException(message));
        });
}

/// <summary>
/// Raised when a package's licence demands a recorded acceptance that is missing, or that was given
/// against an earlier version of the terms. Distinct from
/// <see cref="PackageAuthorizationException"/>: the caller is not unauthorized, they have simply not
/// agreed — and the remedy is to show the terms and record consent, not to acquire a permission.
/// </summary>
public sealed class LicenseAcceptanceRequiredException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="LicenseAcceptanceRequiredException"/> class.</summary>
    public LicenseAcceptanceRequiredException()
    {
    }

    /// <summary>Initializes a new instance with the refusal <paramref name="message"/>.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    public LicenseAcceptanceRequiredException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and an inner exception.</summary>
    /// <param name="message">The speaking refusal reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public LicenseAcceptanceRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
