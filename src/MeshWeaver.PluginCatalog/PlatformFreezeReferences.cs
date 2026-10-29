using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The wire body and stored content of one CI platform-freeze reference (#7378): a satellite lane
/// that runs FROZEN on one platform set (its <c>MW_PLATFORM_REF</c> repository variable) files this
/// on the plugin registry it pulls prebuilt bundles from, so that registry's prebuilt-bundle
/// retention keeps the frozen set however old it is.
///
/// <para>The lane states WHICH set and WHO froze on it; the store stamps the rest. A body's
/// <see cref="RegisteredInstance"/>, <see cref="FiledAt"/> and <see cref="ExpiresAt"/> are
/// discarded and re-stamped by <see cref="PlatformFreezeReferences.File"/>: the sender may only say
/// what it froze on, never for how long the store keeps it.</para>
/// </summary>
public sealed record PlatformFreezeReference
{
    /// <summary>The event discriminator, <see cref="PlatformFreezeReferences.EventName"/>.</summary>
    public string Event { get; init; } = PlatformFreezeReferences.EventName;

    /// <summary>The platform SET the freeze resolved to — the resolver's <c>set</c> output, which is the
    /// platform version a <c>_releases/&lt;version&gt;</c> marker on the store is named after.</summary>
    public string Set { get; init; } = "";

    /// <summary>The freeze as the lane's variable wrote it (a core sha or a set name). Diagnostic.</summary>
    public string? Freeze { get; init; }

    /// <summary>The core commit the set was built from, when the resolver named it. Diagnostic.</summary>
    public string? CoreSha { get; init; }

    /// <summary>The repository whose lane froze (<c>owner/name</c>).</summary>
    public string Repository { get; init; } = "";

    /// <summary>The lane within the repository (workflow and job), so two lanes of one repository
    /// frozen on two sets are two references.</summary>
    public string Lane { get; init; } = "";

    /// <summary>The run that filed the reference (a URL). Diagnostic.</summary>
    public string? Run { get; init; }

    /// <summary>The registry instance id the store AUTHENTICATED the sender as. Stamped by the store.</summary>
    public string? RegisteredInstance { get; init; }

    /// <summary>When the store filed the reference (ISO 8601, UTC). Stamped by the store.</summary>
    public string? FiledAt { get; init; }

    /// <summary>When the reference stops protecting the set (ISO 8601, UTC). Stamped by the store as
    /// <see cref="FiledAt"/> plus <see cref="PlatformFreezeReferences.Lifetime"/>.</summary>
    public string? ExpiresAt { get; init; }
}

/// <summary>
/// CI platform freezes as prebuilt-bundle retention references (#7378,
/// <c>Doc/Architecture/PrebuiltBundleRetention</c>).
///
/// <para>A satellite lane frozen on a platform set pulls that set's bundles from the plugin
/// registry by identity. The registry's retention keeps an identity only when something
/// references it, and a freeze was a value in a GitHub Actions variable: it filed no report, held
/// no record and stamped nothing. Past the 30-day window the frozen set was collectable. Now the
/// lane files a reference here each time it runs frozen, and
/// <see cref="DeploymentPinnedReferences.Read"/> reads every unexpired one as a pin.</para>
///
/// <para>🚨 <b>A reference EXPIRES.</b> The store stamps
/// <see cref="PlatformFreezeReference.ExpiresAt"/> as filing time plus <see cref="Lifetime"/>, and a
/// lane re-files on every frozen run, so a freeze that is still in force keeps its set and one
/// that was lifted stops protecting it a week after its last run. Without the expiry an abandoned
/// freeze would pin a set for ever.</para>
///
/// <para>🚨 <b>It fails closed.</b> A reference whose set or expiry cannot be read REFUSES the
/// whole pass (<see cref="Resolve"/> throws), exactly as an unreadable consumer report does; a
/// reference that is unreadable is not a reference that is absent.</para>
/// </summary>
public static class PlatformFreezeReferences
{
    /// <summary>The node type a filed freeze reference carries.</summary>
    public const string NodeType = "Hosting/PlatformFreeze";

    /// <summary>The <see cref="PlatformFreezeReference.Event"/> a body must carry.</summary>
    public const string EventName = "platform-freeze";

    /// <summary>The route on a plugin REGISTRY a frozen CI lane posts its reference to, presenting
    /// its registry instance key (the same key it pulls bundles with).</summary>
    public const string InboxRoute = "/api/ci/platform-freezes";

    /// <summary>Where, under the operational space (<c>Hosting:OperationalSpace</c>), a store files
    /// freeze references. A node of <see cref="NodeType"/> anywhere else is not read.</summary>
    public const string Segment = "PlatformFreezes";

    /// <summary>How long one filing protects its set. A lane re-files on every frozen run, and a
    /// satellite runs at least daily (its schedule), so a week covers a weekend and an outage of the
    /// lane without letting a lifted freeze pin a set for longer than that.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private static readonly TimeSpan FilingBudget = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Why a body cannot be filed, or null when it can. The set must be named, and so must the
    /// repository and the lane, because they are the reference's key.
    /// </summary>
    /// <param name="body">The body as received.</param>
    public static string? Refusal(PlatformFreezeReference? body)
    {
        if (body is null || !string.Equals(body.Event, EventName, StringComparison.Ordinal))
            return $"the body must be a platform freeze reference (event: {EventName}).";
        if (string.IsNullOrWhiteSpace(body.Set))
            return "the body names no `set` — the platform set the freeze resolved to.";
        if (string.IsNullOrWhiteSpace(body.Repository) || string.IsNullOrWhiteSpace(body.Lane))
            return "the body must name the `repository` and the `lane` that froze.";
        return null;
    }

    /// <summary>
    /// The node id a reference is filed under: the authenticated instance, the repository and the
    /// lane. One lane holds one reference, so a re-filing refreshes it and a freeze moved to another
    /// set replaces it; binding the key to the authenticated sender means one key can never
    /// overwrite (and so un-protect) another sender's freeze.
    /// </summary>
    /// <param name="registeredInstance">The authenticated sender.</param>
    /// <param name="repository">The repository that froze.</param>
    /// <param name="lane">The lane that froze.</param>
    public static string IdOf(string registeredInstance, string repository, string lane) =>
        $"{Slug(registeredInstance)}--{Slug(repository)}--{Slug(lane)}";

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return builder.ToString();
    }

    /// <summary>
    /// The registry half of a CI freeze: files <paramref name="reference"/> at
    /// <c>{Hosting:OperationalSpace}/PlatformFreezes/{id}</c>, stamped with the instance the caller
    /// AUTHENTICATED as, the filing time and the expiry, whatever the body said. Executed as System:
    /// the authorization is the caller's registry credential, checked before this is called.
    /// </summary>
    /// <param name="hub">The registry's mesh hub.</param>
    /// <param name="registeredInstance">The authenticated caller's registry instance id.</param>
    /// <param name="reference">The reference as received (must pass <see cref="Refusal"/>).</param>
    /// <param name="now">The filing instant.</param>
    /// <returns>The path the reference was filed at. Cold.</returns>
    public static IObservable<string> File(
        IMessageHub hub, string registeredInstance, PlatformFreezeReference reference, DateTimeOffset now)
    {
        if (Refusal(reference) is { } refusal)
            return Observable.Throw<string>(new ArgumentException(refusal, nameof(reference)));
        var config = hub.ServiceProvider.GetService<IConfiguration>();
        var stamped = reference with
        {
            Set = reference.Set.Trim(),
            RegisteredInstance = registeredInstance,
            FiledAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ExpiresAt = (now + Lifetime).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        var payload = JsonNode.Parse(JsonSerializer.Serialize(stamped, Json))!.AsObject();
        payload["$type"] = nameof(PlatformFreezeReference);
        var node = new MeshNode(
            IdOf(registeredInstance, reference.Repository, reference.Lane),
            $"{OperationalSpaceOf(config)}/{Segment}")
        {
            Name = $"{reference.Repository} {reference.Lane} — frozen on {stamped.Set}",
            NodeType = NodeType,
            State = MeshNodeState.Active,
            Content = payload,
        };
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return accessService
            .RunAsSystem(() => meshService.CreateOrUpdateNode(node))
            .Take(1)
            .Timeout(FilingBudget)
            .Select(written => written.Path);
    }

    /// <summary>
    /// The pins every unexpired freeze reference contributes. Pure over its inputs.
    ///
    /// <para>Only nodes filed at <paramref name="location"/> are read: the type is a mesh type, and
    /// a node of it written anywhere else is no evidence that a lane is frozen. An expired reference
    /// protects nothing and is logged. A reference whose set or expiry cannot be read refuses the
    /// whole pass.</para>
    /// </summary>
    /// <param name="nodes">Every active node of <see cref="NodeType"/> this host holds.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="now">The instant expiry is judged at.</param>
    /// <param name="location">The filing location, <c>{Hosting:OperationalSpace}/PlatformFreezes</c>.</param>
    /// <param name="logger">Where the verdict is logged, if anywhere.</param>
    /// <exception cref="InvalidOperationException">A reference at the filing location is unreadable.</exception>
    public static ImmutableList<PinnedPlatformReference> Resolve(
        IReadOnlyList<MeshNode> nodes,
        JsonSerializerOptions options,
        DateTimeOffset now,
        string location,
        ILogger? logger = null)
    {
        var references = ImmutableList.CreateBuilder<PinnedPlatformReference>();
        var refusals = ImmutableList.CreateBuilder<string>();
        var expired = 0;
        foreach (var node in nodes)
        {
            if (!string.Equals(node.Namespace, location, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogInformation(
                    "PrebuiltBundleRetention: {Path} is a {NodeType} node outside {Location} and is not read as a freeze",
                    node.Path, NodeType, location);
                continue;
            }
            var set = DeploymentPinnedReferences.Field(node, nameof(PlatformFreezeReference.Set), options);
            var expiresAt = DeploymentPinnedReferences.Field(node, nameof(PlatformFreezeReference.ExpiresAt), options);
            if (string.IsNullOrWhiteSpace(set))
            {
                refusals.Add($"{node.Path}: the freeze reference names no set, so what it protects is unknown");
                continue;
            }
            if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiry))
            {
                refusals.Add($"{node.Path}: the freeze reference on {set} carries no readable expiry");
                continue;
            }
            var repository = DeploymentPinnedReferences.Field(node, nameof(PlatformFreezeReference.Repository), options);
            var lane = DeploymentPinnedReferences.Field(node, nameof(PlatformFreezeReference.Lane), options);
            if (expiry <= now)
            {
                expired++;
                logger?.LogInformation(
                    "PrebuiltBundleRetention: the freeze of {Repository} {Lane} on {Set} expired at {ExpiresAt} "
                    + "({Path}) and no longer protects the set",
                    repository, lane, set, expiry, node.Path);
                continue;
            }
            references.Add(new PinnedPlatformReference(
                $"CI freeze {repository} {lane} ({node.Path}, until {expiry:O})", set.Trim(), null));
        }

        logger?.LogInformation(
            "PrebuiltBundleRetention CI freezes at {Location}: {Active} in force, {Expired} expired{Verdict}",
            location, references.Count, expired,
            refusals.Count == 0 ? "" : $" — {refusals.Count} UNREADABLE");
        if (refusals.Count > 0)
            throw new InvalidOperationException(
                $"{refusals.Count} CI freeze reference(s) could not be read, so the sets they protect are unknown "
                + "and nothing may be collected:"
                + string.Concat(refusals.Select(r => Environment.NewLine + "  • " + r)));
        return references.ToImmutable();
    }

    /// <summary>The filing location on a host: <c>{Hosting:OperationalSpace}/PlatformFreezes</c>.</summary>
    /// <param name="configuration">The host's configuration.</param>
    public static string LocationOf(IConfiguration? configuration) => $"{OperationalSpaceOf(configuration)}/{Segment}";

    private static string OperationalSpaceOf(IConfiguration? configuration)
    {
        var ops = configuration?[DeploymentReportService.OperationalSpaceKey]?.Trim();
        return string.IsNullOrEmpty(ops) ? DeploymentReportService.DefaultOperationalSpace : ops;
    }
}
