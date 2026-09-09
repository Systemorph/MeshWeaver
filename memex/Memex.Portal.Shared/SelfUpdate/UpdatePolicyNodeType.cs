using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// <summary>
    /// The update strategy AS DECLARED on the record — <c>null</c> when the record carries no
    /// <c>policy</c> field at all. Read <see cref="Policy"/> instead; this exists so that "absent"
    /// and "explicitly chosen" stay different facts on the wire (#3542).
    /// </summary>
    [Description("Update strategy")]
    [Translation("de", "Update-Strategie")]
    [JsonPropertyName("policy")]
    public UpdatePolicyKind? DeclaredPolicy { get; init; }

    /// <summary>
    /// The strategy this install actually follows. An ABSENT declaration reads as
    /// <see cref="UpdatePolicyKind.None"/> — never as "enabled" (#3542).
    ///
    /// <para>🚨 Why a nullable backing field rather than reordering the enum, which was the obvious
    /// repair and is WRONG: the hub serializer sets <c>DefaultIgnoreCondition = WhenWritingDefault</c>,
    /// so whichever member is zero is dropped on write. Putting <c>None</c> at zero would have made an
    /// explicit <c>None</c> unwritable — measured, it broke the MCP patch that turns auto-update OFF,
    /// which is the safety-critical direction. A nullable field has <c>null</c> as its default, so
    /// EVERY named member survives the round trip and only genuine absence is dropped.</para>
    ///
    /// <para>The consequence that matters: an install whose record lost its policy under its own
    /// bookkeeping writes no longer rolls itself. That is how memex-cloud reached a withdrawn
    /// <c>3.1.0-ci</c> line "on a policy record that lost its own policy".</para>
    ///
    /// <para>🚨 Reading an absent policy as <c>None</c> made the CONSEQUENCE safe; it did not stop
    /// the record from losing the field. What did is that every bookkeeping write now goes through
    /// the framework's TYPED write (<c>Update&lt;UpdatePolicyContent&gt;</c>), which refuses a node
    /// whose content it cannot read instead of writing a default over it — see
    /// <see cref="UpdatePolicyNodeType.ParseContent"/>.</para>
    /// </summary>
    [JsonIgnore]
    public UpdatePolicyKind Policy
    {
        get => DeclaredPolicy ?? UpdatePolicyKind.None;
        init => DeclaredPolicy = value;
    }

    /// <summary>
    /// 🚨 The version PATTERN that admits continuous builds — a glob over the registry tag, e.g.
    /// <c>3.0.1-ci*</c> (<see cref="UpdateChannelPattern"/>). Read only under
    /// <see cref="UpdatePolicyKind.Continuous"/>: a pre-release tag is eligible ONLY when this
    /// admits it, and <c>Continuous</c> with no pattern is <c>Stable</c> (clean releases only) —
    /// the poller says so once at Warning, naming this field. Under <c>Stable</c> a pattern, when
    /// set, narrows the clean releases considered (e.g. <c>3.0.*</c> keeps an install on one line).
    /// Null or blank = no pattern.
    ///
    /// <para>The fleet's own setting while no clean release above <c>3.0.0</c> exists is
    /// <c>3.0.0-ci*</c>; it stops matching the day <c>3.0.1</c> is tagged, which is the intended
    /// way for "follow the line" to end. See <c>Doc/Architecture/ReleaseProcess</c> §1.</para>
    /// </summary>
    [Description("Version pattern for continuous builds, e.g. 3.0.1-ci*")]
    [Translation("de", "Versionsmuster für Continuous-Builds, z. B. 3.0.1-ci*")]
    [JsonPropertyName("pattern")]
    public string? Pattern { get; init; }

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

    /// <summary>
    /// The tag the availability gate most recently evaluated and had something to SAY about
    /// without deciding on it (#3651) — held or not. Written beside <see cref="Advisories"/> on
    /// every gate evaluation, so the Updates tab renders the advisories only for the tag they
    /// describe. Not user-editable.
    /// </summary>
    [Browsable(false)]
    public string? AdvisoriesTag { get; init; }

    /// <summary>
    /// 🚨 What the gate reported about <see cref="AdvisoriesTag"/> WITHOUT holding on it (#3651,
    /// <c>UpdatabilityVerdict.Advisories</c>), one line per item joined with <c>"; "</c>: the
    /// packages that would recompile at boot because no bake is sealed for the target, a landed
    /// module whose loadability on the target could not be measured, a declared platform floor
    /// the target does not rank above. Cleared (null) when the last evaluation had nothing to say.
    /// This exists so a roll that compiles content at boot is a VISIBLE state on the tab an
    /// operator already looks at, not a fact that lives only in a pod log. Not user-editable.
    /// </summary>
    [Browsable(false)]
    public string? Advisories { get; init; }

    /// <summary>Whether <paramref name="tag"/> is the tag <see cref="Advisories"/> describe.</summary>
    public bool HasAdvisoriesFor(string? tag) =>
        !string.IsNullOrEmpty(tag)
        && !string.IsNullOrEmpty(Advisories)
        && string.Equals(AdvisoriesTag, tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 🚨 The version this install RUNS, when the last check found that it is no longer published in
    /// the registry (#3543); <c>null</c> otherwise. Written by the poller on EVERY check, so it is
    /// cleared the moment the tag resolves again — the same unconditional-clearing rule the
    /// availability hold follows, and for the same reason: a healed state that lingers is a stale
    /// scare.
    ///
    /// <para>This exists because the state was INVISIBLE. An install stranded on a withdrawn tag and
    /// a perfectly up-to-date one produced byte-identical evidence — an empty
    /// <see cref="LatestAvailableTag"/> and "no newer release" — while the first cannot start a new
    /// pod at all and can never be rescued by a publication, since nothing outranks a tag that
    /// already outranks everything left. Not user-editable.</para>
    /// </summary>
    [Browsable(false)]
    public string? UnresolvedInstalledTag { get; init; }

    /// <summary>
    /// Whether a hold RECORD exists for <paramref name="tag"/>. 🚨 This is a question about the
    /// record, NOT about why the install is standing still — see <see cref="IsHoldOperative"/>
    /// before rendering it as a reason (#3812).
    /// </summary>
    public bool IsHeld(string? tag) =>
        !string.IsNullOrEmpty(tag)
        && string.Equals(HeldTag, tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 🚨 <b>Whether the hold on <paramref name="tag"/> is a LIVE verdict — one the checker that
    /// wrote it would still clear (#3812).</b>
    ///
    /// <para><see cref="IsHeld"/> answers "is there a hold record"; both readers of it were asking
    /// "is this why the install is not moving right now", and those stopped being the same question
    /// the moment a check could decline to evaluate. <see cref="HeldAt"/> and
    /// <see cref="HeldReason"/> are written ONLY by the poller's hold write, which runs only when a
    /// candidate is actually evaluated; <see cref="LastCheckedAt"/> is written on EVERY check. Under
    /// <see cref="UpdatePolicyKind.None"/> the check records "updates are disabled" and evaluates
    /// nothing — so the hold is never revisited, never cleared, and the fresh
    /// <see cref="LastCheckedAt"/> beside it makes a two-day-old record read as a current
    /// verdict.</para>
    ///
    /// <para>Measured on memex 2026-09-09: <c>heldAt</c> 2026-09-07T22:27Z, <c>lastCheckedAt</c>
    /// 2026-09-09T10:41Z, and the reason quoted three module floors that #3648 had stopped holding
    /// on two hours after that <c>heldAt</c>. #3706 was filed on that frozen record and read it as
    /// live. #3795 sharpened it: under <c>None</c> the build-completion triggers are now filtered
    /// out before they check, so nothing revisits the hold at all.</para>
    ///
    /// <para>🚨 The record is deliberately NOT cleared when updates are disabled — the last real
    /// evaluation is the only diagnostic an operator has when they turn updates back on. What
    /// changes is that a surface must present it as history, and say the current reason (updates
    /// are disabled) as the current reason.</para>
    /// </summary>
    public bool IsHoldOperative(string? tag) =>
        IsHeld(tag) && Policy != UpdatePolicyKind.None;

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
    /// admin-chosen policy is preserved). <paramref name="defaultPattern"/> is seeded beside the
    /// policy (<see cref="UpdatePolicyContent.Pattern"/>); null seeds none.
    /// </summary>
    public static IObservable<string> EnsureExists(
        IMessageHub hub, AccessService? accessService, UpdatePolicyKind defaultPolicy, ILogger? logger = null,
        string? defaultPattern = null)
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
            Content = new UpdatePolicyContent
            {
                Policy = defaultPolicy,
                Pattern = UpdateChannelPattern.Normalize(defaultPattern),
            },
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
                        "[SelfUpdate] seeding {Path} = {Policy} (pattern: {Pattern}).",
                        NodePath, defaultPolicy, UpdateChannelPattern.Normalize(defaultPattern) ?? "none");
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
    ///
    /// <para>🚨 The TYPED write, never <c>ParseContent</c> + the untyped overload — see
    /// <see cref="ParseContent"/> for what that shape destroys.</para>
    /// </summary>
    public static IObservable<Unit> RecordVerification(IMessageHub hub, ComboVerification verdict)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        return Observable.Create<Unit>(observer =>
        {
            using (AccessContextScope.AsSystem(accessService))
                return hub.GetWorkspace().GetMeshNodeStream(NodePath)
                    .Update<UpdatePolicyContent>((node, cur) =>
                    {
                        var current = cur ?? new UpdatePolicyContent();
                        var verifications = current.ComboVerifications
                            .RemoveAll(v => string.Equals(
                                v.CandidateTag, verdict.CandidateTag,
                                StringComparison.OrdinalIgnoreCase))
                            .Add(verdict)
                            .OrderByDescending(v => v.VerifiedAt)
                            .Take(MaxRecordedVerifications)
                            .ToImmutableList();
                        return node with
                        {
                            Content = current with { ComboVerifications = verifications },
                        };
                    })
                    .Select(_ => Unit.Default)
                    .Subscribe(observer);
        });
    }

    /// <summary>Parses the policy content from a node (handles typed content, a degraded
    /// <see cref="JsonElement"/>, an as-written <c>JsonNode</c> and a same-named record from another
    /// build); returns defaults when absent or unreadable. 🚨 READ-ONLY — see
    /// <see cref="ParseContent"/>.</summary>
    public static UpdatePolicyContent Parse(MeshNode? node, JsonSerializerOptions options) =>
        ParseContent(node?.Content, options);

    /// <summary>
    /// Parses the policy content from a node's <c>Content</c> value, defaulting when it is absent
    /// or cannot be read.
    ///
    /// <para>🚨 <b>READ-ONLY. A WRITE MUST NEVER BE BUILT ON THIS.</b> Every write on
    /// <c>Admin/UpdatePolicy</c> is the framework's TYPED write —
    /// <c>stream.Update&lt;UpdatePolicyContent&gt;((node, cur) =&gt; node with { Content = (cur ??
    /// new UpdatePolicyContent()) with { … } })</c> — and never
    /// <c>Update(node =&gt; … ParseContent(node.Content, …) …)</c>.</para>
    ///
    /// <para>A read must stay bad-data tolerant: a settings tab that throws is worse than one
    /// showing the fail-closed <see cref="UpdatePolicyKind.None"/>. That tolerance is exactly what
    /// makes it wrong for a write. This method answers <c>new UpdatePolicyContent()</c> for BOTH
    /// "there is no content" and "the content is present and this build cannot read it", and a
    /// bookkeeping write built on it PERSISTS that empty record — a failed READ becoming a write
    /// that erases the admin's policy, the latest available tag, every combo verdict and any live
    /// availability hold. That is #3542's proposal 3, and it survived #3607, which changed only
    /// what an ABSENT policy MEANS.</para>
    ///
    /// <para>Measured on <c>origin/main</c> at <c>5453be493</c>: with the record holding
    /// <c>{"policy":"None","latestAvailableTag":"3.0.0-ci.8009","comboVerifications":"corrupt"}</c>
    /// — one field this build cannot deserialize — one <c>RecordAvailable</c>-shaped write
    /// completed silently and left
    /// <c>{"requireCiGreen":true,"latestAvailableTag":"3.0.0-ci.9999","comboVerifications":[]}</c>.
    /// The <c>policy</c> field was gone: precisely the production state #3542 opens on.</para>
    ///
    /// <para>The typed overload is the framework's own answer to exactly this and needs no local
    /// guard: <c>null</c> there means ABSENT and only absent, while content that is present but
    /// unreadable faults the observable with a <c>MeshNodeStreamException</c> carrying the path,
    /// the runtime type and a JSON excerpt — <b>the write does not happen</b>. Every bookkeeping
    /// caller already wraps its write in a <c>.Catch</c> that logs and carries on (#1020: a
    /// bookkeeping write may never gate the roll), so an unreadable record is refused loudly and
    /// left intact instead of overwritten silently.</para>
    /// </summary>
    public static UpdatePolicyContent ParseContent(object? content, JsonSerializerOptions options) =>
        // ObjectAsExtensions.As<T>, not a hand-rolled JsonElement switch: it also covers the
        // as-written JsonNode DOM and a same-named record from another collectible assembly, both
        // of which the switch answered with a silent default.
        content.As<UpdatePolicyContent>(options) ?? new UpdatePolicyContent();

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
