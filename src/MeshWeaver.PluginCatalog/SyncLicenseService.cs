using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Issues and revokes an instance's <b>sync licence</b> — the right of a registered
/// <see cref="MeshWeaverInstance"/> to replicate a package from this registry, recorded as entries
/// on its <see cref="PluginGrant"/>.
///
/// <para><b>Why this exists.</b> Until now a grant had exactly two writers: the
/// <c>DefaultGrants</c> seed at registration, and an admin typing into the Instance-grants tab.
/// Neither can express a licence — no term, no issuing terms, no record of what the right was
/// granted FOR — and hand-editing one node per consumer does not scale past a handful of
/// instances. Every issuer (the admin tab, a fulfilled order, a redeemed coupon, an automated
/// provisioning step) funnels through here instead, so a licence is issued ONE way and carries its
/// terms wherever it came from.</para>
///
/// <para>🚨 <b>This service records an authorization; it does not make one.</b> The decision that
/// the licence MAY be issued — a global-admin gate, a verified payment, a validated coupon — belongs
/// to the caller, exactly as <see cref="PackageEntitlement"/> puts the check on the action. What is
/// enforced here is that the decision is ATTRIBUTABLE: an issue with no issuing principal is refused
/// rather than written under a blank name, because a right nobody is recorded as having granted
/// cannot be reviewed or revoked with confidence.</para>
///
/// <para>Writes run under the System identity: grants live in the <b>Admin</b> partition, which is
/// deliberately not writable by the instance's owner (self-service registration plus admin-owned
/// grants is what stops registration from becoming self-service access). Read-then-
/// <see cref="IMeshService.CreateOrUpdateNode"/>, never <c>stream.Update</c> — an instance's FIRST
/// licence has no node yet, and Update on a missing path aborts.</para>
/// </summary>
public sealed class SyncLicenseService(IMessageHub hub, ILogger<SyncLicenseService> logger)
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Issues (or re-issues) a sync licence for one <c>(source, package)</c> pair. Idempotent: an
    /// existing entry for the same pair is REPLACED, so renewing a term or correcting the terms is
    /// the same call as granting it the first time — never a second entry that shadows the first.
    /// </summary>
    /// <param name="request">What is being licensed, to whom, under which terms, by whom.</param>
    /// <returns>The stored grant after the write.</returns>
    /// <exception cref="ArgumentException">The request is not attributable or not addressable.</exception>
    public IObservable<PluginGrant> Issue(SyncLicenseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.InstanceId, nameof(request.InstanceId));
        Require(request.Source, nameof(request.Source));
        Require(request.PackageId, nameof(request.PackageId));
        // Attribution is not optional — see the type remarks.
        Require(request.IssuedByUserId, nameof(request.IssuedByUserId));

        var entry = new PluginGrantEntry
        {
            Source = request.Source,
            PackageId = request.PackageId,
            Tier = PlanTierRanks.Canonical(request.Tier) is { Length: > 0 } plan ? plan : null,
            ExpiresAt = request.ExpiresAt,
            IssuedUnderLicense = request.IssuedUnderLicense,
            IssuedVia = request.IssuedVia,
            IssuedAt = request.IssuedAt ?? DateTimeOffset.UtcNow,
        };

        return Mutate(request.InstanceId, request.IssuedByUserId, grant => grant with
        {
            Entries = Without(grant.Entries, request.Source, request.PackageId).Append(entry).ToList(),
        })
        .Do(_ => logger.LogInformation(
            "Sync licence issued: {Entry} → {InstanceId} (licence {License}, via {Via}, expires {Expires})",
            entry, request.InstanceId, request.IssuedUnderLicense ?? "unspecified",
            request.IssuedVia ?? "unspecified",
            request.ExpiresAt?.ToString("O") ?? "never"));
    }

    /// <summary>
    /// Withdraws the licence for one <c>(source, package)</c> pair by removing its entry. Removing
    /// an absent entry is a no-op that still completes — revocation is idempotent, so a retry after
    /// a partial failure is safe.
    /// </summary>
    public IObservable<PluginGrant> Revoke(
        string instanceId, string source, string packageId, string revokedByUserId)
    {
        Require(instanceId, nameof(instanceId));
        Require(source, nameof(source));
        Require(packageId, nameof(packageId));
        Require(revokedByUserId, nameof(revokedByUserId));

        return Mutate(instanceId, revokedByUserId, grant => grant with
        {
            Entries = Without(grant.Entries, source, packageId).ToList(),
        })
        .Do(_ => logger.LogInformation("Sync licence revoked: {Source}/{Package} → {InstanceId}",
            source, packageId, instanceId));
    }

    /// <summary>
    /// The instance-wide stop: flips <see cref="PluginGrant.IsRevoked"/> so the grant authorizes
    /// nothing, while every entry and its recorded terms stay intact.
    ///
    /// <para>Deliberately NOT "delete the entries". A revocation has to be reviewable afterwards —
    /// what was licensed, under what terms, and who ended it — and an emptied list answers none of
    /// that. It is also reversible with <see cref="Reinstate"/>, which deleting is not.</para>
    /// </summary>
    public IObservable<PluginGrant> RevokeAll(string instanceId, string revokedByUserId)
    {
        Require(instanceId, nameof(instanceId));
        Require(revokedByUserId, nameof(revokedByUserId));

        return Mutate(instanceId, revokedByUserId, grant => grant with { IsRevoked = true })
            .Do(_ => logger.LogWarning("Sync licence REVOKED wholesale for {InstanceId} by {User}",
                instanceId, revokedByUserId));
    }

    /// <summary>Lifts a wholesale revocation. Entries that have since expired stay expired — this
    /// clears the kill switch, it does not renew a term.</summary>
    public IObservable<PluginGrant> Reinstate(string instanceId, string reinstatedByUserId)
    {
        Require(instanceId, nameof(instanceId));
        Require(reinstatedByUserId, nameof(reinstatedByUserId));

        return Mutate(instanceId, reinstatedByUserId, grant => grant with { IsRevoked = false })
            .Do(_ => logger.LogInformation("Sync licence reinstated for {InstanceId} by {User}",
                instanceId, reinstatedByUserId));
    }

    /// <summary>
    /// Read → transform → write, under System, for one instance's grant node. The prior read is a
    /// one-shot by EXACT PATH, never a query: a query is eventually consistent and would happily
    /// drop an entry another issuer added moments ago.
    ///
    /// <para>🚨 <see cref="ImpersonationScopeExtensions.RunAsSystem{T}"/>, never
    /// <c>Observable.Using(access.ImpersonateAsSystem, …)</c> (#1790). Rx runs a Using factory on the
    /// SUBSCRIBING thread and disposes on termination, which leaves the subscriber latched as System
    /// — here that subscriber is an admin surface or an HTTP request, so the latch would hand it
    /// <c>Permission.All</c>. RunAsSystem seals both ends and delivers notifications under the
    /// subscriber's own identity; the WIDEST cold pipeline goes inside the factory, so the whole
    /// read-transform-write runs as System and nothing downstream inherits it.</para>
    /// </summary>
    private IObservable<PluginGrant> Mutate(
        string instanceId, string actingUserId, Func<PluginGrant, PluginGrant> transform)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var path = MeshWeaverInstanceNodeType.GrantPath(instanceId);

        return accessService.RunAsSystem(() => hub.GetMeshNode(path, ReadTimeout)
            .Take(1)
            .SelectMany(existing => Observable.Defer(() =>
            {
                var current = existing?.ContentAs<PluginGrant>(hub.JsonSerializerOptions)
                              ?? new PluginGrant { InstanceId = instanceId };
                var updated = transform(current) with
                {
                    InstanceId = instanceId,
                    GrantedByUserId = actingUserId,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                var node = new MeshNode(instanceId, MeshWeaverInstanceNodeType.GrantNamespace)
                {
                    Name = $"Plugin grant: {instanceId}",
                    NodeType = MeshWeaverInstanceNodeType.GrantNodeType,
                    State = MeshNodeState.Active,
                    Content = updated,
                };
                return meshService.CreateOrUpdateNode(node).Select(_ => updated);
            })));
    }

    /// <summary>Entries with the given <c>(source, package)</c> pair removed — the same matching
    /// rule the admin tab uses, so "replace" means the same thing everywhere.</summary>
    private static IEnumerable<PluginGrantEntry> Without(
        IEnumerable<PluginGrantEntry> entries, string source, string packageId) =>
        entries.Where(e => !(string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(e.PackageId, packageId, StringComparison.Ordinal)));

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required to issue or revoke a sync licence.", name);
    }
}

/// <summary>
/// One sync-licence issuance: what is licensed, to which instance, under which terms, on whose
/// authority. A record rather than a parameter list because every field is part of the audit trail
/// and they are routinely carried together from an order or a coupon.
/// </summary>
public sealed record SyncLicenseRequest
{
    /// <summary>The <see cref="MeshWeaverInstance.InstanceId"/> being licensed.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The registry source's configured name (<c>Plugins</c>, <c>Education</c>).</summary>
    public required string Source { get; init; }

    /// <summary>The package id, or <see cref="PluginGrantEntry.AllPackages"/> for the whole
    /// source.</summary>
    public required string PackageId { get; init; }

    /// <summary>ObjectId of the principal that authorized this issuance. Required — an
    /// unattributable grant is refused rather than written anonymously.</summary>
    public required string IssuedByUserId { get; init; }

    /// <summary>End of the term. Null = perpetual (revocation remains available).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The subscription plan the entry is scoped to (<see cref="PluginGrantEntry.Tier"/>),
    /// or null/blank for every tier.</summary>
    public string? Tier { get; init; }

    /// <summary>SPDX id of the licence the right is issued under. Null stays null — never
    /// defaulted, since recording terms nobody granted is worse than recording none.</summary>
    public string? IssuedUnderLicense { get; init; }

    /// <summary>How this came about — an order id, a coupon code, a ticket reference.</summary>
    public string? IssuedVia { get; init; }

    /// <summary>When it was issued; defaults to the moment of the write. Explicit only so a
    /// backfill can record the real date rather than the migration's.</summary>
    public DateTimeOffset? IssuedAt { get; init; }
}
