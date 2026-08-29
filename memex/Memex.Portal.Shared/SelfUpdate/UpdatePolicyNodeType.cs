using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.SelfUpdate;   // UpdatePolicyKind — the policy value

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Content of the <c>Admin/UpdatePolicy</c> node: the admin-editable update <see cref="Policy"/>
/// plus the self-update poller's bookkeeping (the latest tag it has seen on the registry and when).
/// Edited via the standard node-content editor (the <see cref="Policy"/> enum renders as a dropdown);
/// the two bookkeeping fields are written by the poller as System and hidden from the editor.
/// </summary>
public record UpdatePolicyContent
{
    /// <summary>The update strategy. Defaults to <see cref="UpdatePolicyKind.Continuous"/>.</summary>
    [Description("Update strategy")]
    [Translation("de", "Update-Strategie")]
    public UpdatePolicyKind Policy { get; init; } = UpdatePolicyKind.Continuous;

    /// <summary>
    /// When <c>true</c> (default) the install only rolls to builds that PASSED CI ("green").
    /// The continuous-delivery pipeline already publishes an image ONLY when "MeshWeaver Build and
    /// Test" succeeds, so the verified channel contains green builds exclusively; this flag is the
    /// forward-looking guard that keeps that guarantee if an "edge" channel (publish-on-every-build,
    /// tags carrying the <c>edge</c> pre-release label) is ever added — green-only ignores those.
    /// Set <c>false</c> to also accept unverified edge builds (bleeding-edge / pre-merge testing).
    /// </summary>
    [Description("Only update to CI-verified (green) builds")]
    [Translation("de", "Nur auf CI-geprüfte (grüne) Builds aktualisieren")]
    public bool RequireCiGreen { get; init; } = true;

    /// <summary>The newest image tag the poller has found on the registry (for the admin UI /
    /// detect-and-notify). Written by the poller; not user-editable.</summary>
    [Browsable(false)]
    public string? LatestAvailableTag { get; init; }

    /// <summary>When the poller last recorded <see cref="LatestAvailableTag"/>.</summary>
    [Browsable(false)]
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>
    /// 🚨 When this install last COMPLETED an update check — whatever the check concluded.
    ///
    /// <para>Distinct from <see cref="CheckedAt"/>, and the difference is the defect it closes
    /// (#2553). <see cref="CheckedAt"/> is stamped beside <see cref="LatestAvailableTag"/>, so it
    /// only ever advances on a check that FOUND something newer. An install that checks hourly and
    /// finds nothing therefore has no record of ever having checked — indistinguishable from an
    /// install whose checker is dead, which is precisely the state memex was in for 7 h while its
    /// Updates tab read "No newer version detected yet."</para>
    ///
    /// <para>Written by the poller on EVERY check, on every outcome path, as a best-effort
    /// bookkeeping write that can never gate the roll (#1020). Not user-editable.</para>
    /// </summary>
    [Browsable(false)]
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>The one-sentence verdict of the check <see cref="LastCheckedAt"/> stamps — see
    /// <c>SelfUpdateVerdict</c>. Durable on purpose: a log line depends on a per-category log level
    /// that a deployment may simply not have set (and had not), a node write does not. Not
    /// user-editable.</summary>
    [Browsable(false)]
    public string? LastCheckVerdict { get; init; }

    /// <summary>What woke that check — <c>Startup</c>, <c>BuildCompletion</c>, <c>PolicyChange</c>
    /// or <c>SafetyNet</c>. An install whose checks are ONLY ever <c>SafetyNet</c> has a dead event
    /// channel, and that is not visible from anything else. Not user-editable.</summary>
    [Browsable(false)]
    public string? LastCheckTrigger { get; init; }

    /// <summary>
    /// Combo-verification verdicts per candidate tag — what the Candidate Release Protocol's
    /// instance gate found when it ran this instance's module set inside a candidate image
    /// (<c>mw-combo-verify</c>). Written via
    /// <see cref="UpdatePolicyNodeType.RecordVerification"/> (upsert by tag, newest first, bounded
    /// at <see cref="UpdatePolicyNodeType.MaxRecordedVerifications"/>); the admin Updates tab joins
    /// this against <see cref="LatestAvailableTag"/> so a blocked upgrade reads "cannot update to
    /// X — these modules do not compile/test against it" instead of an eternal "update available".
    /// Not user-editable.
    /// </summary>
    [Browsable(false)]
    public ImmutableList<ComboVerification> ComboVerifications { get; init; } = [];

    /// <summary>
    /// The tag the release-availability gate (#1754) most recently REFUSED, or null when nothing is
    /// held. Written by the poller beside <see cref="HeldReason"/>; not user-editable.
    ///
    /// <para>🚨 This exists so a hold is a VISIBLE, recoverable state rather than a silence. An
    /// instance that quietly stops updating for weeks because one stale package blocks it is its own
    /// outage — worse than the roll it prevented, because nothing shows it happened.</para>
    /// </summary>
    [Browsable(false)]
    public string? HeldTag { get; init; }

    /// <summary>Why <see cref="HeldTag"/> was refused, in one sentence naming the package(s).
    /// Not user-editable.</summary>
    [Browsable(false)]
    public string? HeldReason { get; init; }

    /// <summary>
    /// True when the hold is "the catalogue could not be read" rather than "a package cannot survive
    /// this release". Kept apart because they are different incidents with different fixes, and a UI
    /// that blurred them would send an operator to re-bake something that was never broken.
    /// </summary>
    [Browsable(false)]
    public bool HeldIndeterminate { get; init; }

    /// <summary>When the hold was last recorded — so a stale hold is recognisable as stale.</summary>
    [Browsable(false)]
    public DateTimeOffset? HeldAt { get; init; }

    /// <summary>Whether <paramref name="tag"/> is the tag currently held by the availability gate.</summary>
    public bool IsHeld(string? tag) =>
        !string.IsNullOrEmpty(tag)
        && string.Equals(HeldTag, tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>The recorded verdict for <paramref name="tag"/>, when one exists.</summary>
    public ComboVerification? VerificationFor(string? tag) =>
        string.IsNullOrEmpty(tag)
            ? null
            : ComboVerifications.FirstOrDefault(v =>
                string.Equals(v.CandidateTag, tag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The <c>Admin/UpdatePolicy</c> singleton node — the platform's auto-update strategy. Mirrors
/// <c>AiSettingsNodeType</c> (typed-content node + storm-safe create-on-absent via a query, never a
/// point-read of a maybe-absent path) and the Admin-partition anchoring of
/// <c>ShippedReleaseSeed.PlatformVersionNode</c>.
/// </summary>
public static class UpdatePolicyNodeType
{
    /// <summary>NodeType discriminator.</summary>
    public const string NodeType = "UpdatePolicy";

    /// <summary>The Admin partition that holds platform-level data (schema <c>admin</c>).</summary>
    public const string AdminPartition = ShippedReleaseSeed.AdminPartition;

    /// <summary>The singleton instance id.</summary>
    public const string NodeId = "UpdatePolicy";

    /// <summary>Full path of the policy node: <c>Admin/UpdatePolicy</c>.</summary>
    public const string NodePath = $"{AdminPartition}/{NodeId}";

    /// <summary>Registers the UpdatePolicy node type + its content type for typed (de)serialization.</summary>
    public static TBuilder AddUpdatePolicyType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.ConfigureHub(config => config.WithType<UpdatePolicyContent>(nameof(UpdatePolicyContent)));
        return builder;
    }

    /// <summary>MeshNode TYPE DEFINITION for <c>nodeType:UpdatePolicy</c>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Update Policy",
        Icon = "/static/NodeTypeIcons/rocket.svg",
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<UpdatePolicyContent>())
    };

    /// <summary>
    /// Create-on-absent (idempotent, reactive, as System) of <c>Admin/UpdatePolicy</c> with the given
    /// default policy. Existence is read via <c>GetQuery</c> (empty-on-absent) — NEVER a point
    /// <c>GetMeshNodeStream(path)</c> probe of the maybe-absent node (which NotFound-resubscribe-storms
    /// on a fresh DB). Emits the node path when it exists. An existing node is left untouched (its
    /// admin-chosen policy is preserved).
    /// </summary>
    public static IObservable<string> EnsureExists(
        IMessageHub hub, AccessService? accessService, UpdatePolicyKind defaultPolicy, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(NodePath);
        var workspace = hub.GetWorkspace();

        MeshNode BuildNode() => new(NodeId, AdminPartition)
        {
            NodeType = NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = new UpdatePolicyContent { Policy = defaultPolicy },
        };

        // 🚨 RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` (#1444/#1790):
        // `AsSystem(x)` IS `x.ImpersonateAsSystem()`, so the helper hides the shape rather than
        // changing it. RecordVerification below already states the reason — impersonation is an
        // AsyncLocal and `Using` disposes it on whichever thread the sequence terminates on, leaving
        // the caller latched as System — and hand-rolls the seal with Observable.Create; this is the
        // same seal, taken off the framework shelf.
        return accessService.RunAsSystem(
            () => workspace
                .GetQuery($"{NodeType}|{NodePath}", $"path:{NodePath} nodeType:{NodeType}")
                .Take(1)
                .SelectMany(nodes =>
                {
                    var existing = nodes.FirstOrDefault(n =>
                        string.Equals(n.NodeType, NodeType, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                        return Observable.Return(NodePath);
                    logger?.LogInformation(
                        "[SelfUpdate] seeding {Path} = {Policy}.", NodePath, defaultPolicy);
                    return meshService.CreateNode(BuildNode())
                        .Select(_ => NodePath)
                        // Idempotent: a concurrent first-writer (other replica) won the create race.
                        .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                            ? Observable.Return(NodePath)
                            : Observable.Throw<string>(ex));
                }));
    }

    /// <summary>How many combo verdicts <c>Admin/UpdatePolicy</c> retains. Candidates supersede
    /// fast; the node must never grow without bound.</summary>
    public const int MaxRecordedVerifications = 8;

    /// <summary>
    /// Records a combo-verification verdict on <c>Admin/UpdatePolicy</c> (as System — the same
    /// identity the poller's bookkeeping writes under): upsert by
    /// <see cref="ComboVerification.CandidateTag"/>, newest <see cref="ComboVerification.VerifiedAt"/>
    /// first, bounded at <see cref="MaxRecordedVerifications"/>. Touches ONLY the verification
    /// field; the admin-chosen policy is preserved. Cold — subscribe to run the write.
    ///
    /// <para>🚨 The System scope is opened and closed SYNCHRONOUSLY around the subscribe (the
    /// <see cref="PlatformUpdateStatus"/> shape, and for its reason: impersonation is an
    /// <c>AsyncLocal</c>, and <c>Observable.Using</c> would dispose it on whichever thread the
    /// sequence terminates on, leaving the caller running as System).</para>
    /// </summary>
    public static IObservable<Unit> RecordVerification(IMessageHub hub, ComboVerification verdict)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var jsonOptions = hub.JsonSerializerOptions;
        return Observable.Create<Unit>(observer =>
        {
            using (AccessContextScope.AsSystem(accessService))
                return hub.GetWorkspace().GetMeshNodeStream(NodePath)
                    .Update(node =>
                    {
                        var cur = ParseContent(node.Content, jsonOptions);
                        var verifications = cur.ComboVerifications
                            .RemoveAll(v => string.Equals(
                                v.CandidateTag, verdict.CandidateTag,
                                StringComparison.OrdinalIgnoreCase))
                            .Add(verdict)
                            .OrderByDescending(v => v.VerifiedAt)
                            .Take(MaxRecordedVerifications)
                            .ToImmutableList();
                        return node with
                        {
                            Content = cur with { ComboVerifications = verifications },
                        };
                    })
                    .Select(_ => Unit.Default)
                    .Subscribe(observer);
        });
    }

    /// <summary>Parses the policy content from a node (handles both typed content and raw
    /// <see cref="JsonElement"/>); returns defaults when absent/unparseable.</summary>
    public static UpdatePolicyContent Parse(MeshNode? node, JsonSerializerOptions options) =>
        ParseContent(node?.Content, options);

    /// <summary>Parses the policy content from a node's <c>Content</c> value.</summary>
    public static UpdatePolicyContent ParseContent(object? content, JsonSerializerOptions options) =>
        content switch
        {
            UpdatePolicyContent c => c,
            JsonElement je => TryDeserialize(je, options) ?? new UpdatePolicyContent(),
            _ => new UpdatePolicyContent(),
        };

    private static UpdatePolicyContent? TryDeserialize(JsonElement je, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<UpdatePolicyContent>(je.GetRawText(), options); }
        catch { return null; }
    }

    /// <summary>True if the exception (or any inner) reports an "already exists" outcome — the
    /// idempotent-create success signal.</summary>
    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }
}
