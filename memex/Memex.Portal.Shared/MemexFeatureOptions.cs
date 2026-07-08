using MeshWeaver.Mesh;

namespace Memex.Portal.Shared;

/// <summary>
/// Deploy-time capability toggles for a Memex deployment, bound from the
/// <c>Features</c> configuration section. These declare which capabilities a
/// deployment ships — independent of whether a given key happens to be present.
/// A disabled flag is the operator's intent and wins even if a key is configured.
///
/// All flags default to <c>true</c> so an absent <c>Features</c> section preserves
/// the current behaviour (no regression for existing deployments). Operators turn
/// capabilities OFF explicitly. The env-var form
/// (<c>Features__Ai__Providers__OpenAI=false</c>) flows identically through ACA env,
/// compose <c>.env</c>, and ARM <c>createUiDefinition</c> → container env.
/// </summary>
public sealed record MemexFeatureOptions
{
    public const string SectionName = "Features";

    public AiFeatureOptions Ai { get; init; } = new();

    /// <summary>Static-repo → DB sync: which partitions are materialized into and served from the DB.</summary>
    public StaticRepoSyncFeatureOptions StaticRepoSync { get; init; } = new();

    /// <summary>User self-provisioning (open vs closed registration).</summary>
    public OnboardingFeatureOptions Onboarding { get; init; } = new();

    /// <summary>Orleans clustering provider selection (membership store).</summary>
    public OrleansFeatureOptions Orleans { get; init; } = new();

    /// <summary>
    /// SignalR mesh transport (<c>/signalr</c>) for external participants — native clients, and a
    /// future native iOS portal. On by default (like the other flags); an operator can close the
    /// connection surface with <c>Features:SignalR=false</c>. See Doc/Architecture/SignalRMeshParticipant.
    /// </summary>
    public bool SignalR { get; init; } = true;

    /// <summary>
    /// gRPC mesh transport (<c>meshweaver.v1.Mesh/Open</c> + gRPC-web) for foreign-language
    /// participants (Python/Node workers) and the browser React GUI's Connect+Deliver split.
    /// On by default, symmetric with <see cref="SignalR"/>; close with <c>Features:Grpc=false</c>.
    /// </summary>
    public bool Grpc { get; init; } = true;

    /// <summary>
    /// True when the deployment ships at least one in-process API provider OR one
    /// co-hosted CLI. When false, the portal has no built-in chat capability via
    /// catalog sources (users may still bring their own keys via ModelProviders) —
    /// surfaced as a startup warning, not a hard failure.
    /// </summary>
    public bool HasAnyChatCapability => Ai.Providers.HasAny || Ai.Clis.HasAny;
}

/// <summary>
/// Static-repo → DB synchronization. Selects which partitions' build-time static content
/// (embedded docs, built-in agents, the model catalog) is <b>materialized into and served from
/// the database partition</b> via the static-repo import on boot — instead of the in-memory
/// read-only static provider. For a listed partition the import registers an
/// <c>IStaticRepoSource</c>, the read-only <c>StaticNodePartitionStorageProvider</c> is NOT
/// registered (so Postgres serves + accepts the import's writes), and <c>ImportAll</c> runs after
/// schema provisioning. See <c>Doc/Architecture/StaticRepoImport.md</c>.
///
/// <para>Empty (default) = no sync: every partition keeps the in-memory static provider, no DB
/// import — i.e. current behaviour, no regression. The default Helm deployment sets
/// <c>["Doc","Agent","Provider","Harness","Skill"]</c>. Gated, not global — the monolith (no
/// Postgres) leaves this empty and keeps in-memory serving.</para>
/// </summary>
public sealed record StaticRepoSyncFeatureOptions
{
    /// <summary>
    /// Partition names to materialize into + serve from the DB (e.g. <c>"Doc"</c>, <c>"Agent"</c>,
    /// <c>"Provider"</c>). Matching is case-insensitive. <c>"Provider"</c> is the model catalog
    /// (providers + models + policy); the legacy <c>"Model"</c> name is still honoured as an alias.
    /// Empty = no sync.
    /// </summary>
    public string[] Partitions { get; init; } = [];

    /// <summary>
    /// Optional per-partition <see cref="PartitionSyncMode"/> override, keyed by partition name
    /// (matched case-insensitively at read). Selects what a partition's import PRUNES after upserting
    /// the source: <c>FullReplace</c> (mirror — prune all extras), <c>Additive</c> (prune only
    /// previously-shipped nodes, so user-added nodes survive), or <c>UpsertOnly</c> (never prune). A
    /// partition NOT listed uses its source's own default — <c>FullReplace</c> for most, but the built-in
    /// AI catalogs (Skill/Agent/Provider/Harness) default to <c>Additive</c>. Bind by name, e.g.
    /// <c>Features__StaticRepoSync__Modes__Skill=UpsertOnly</c>. Distinct from the per-NODE
    /// <c>SyncBehavior</c>, which still claims individual nodes in any mode.
    /// </summary>
    public Dictionary<string, PartitionSyncMode> Modes { get; init; } = new();

    /// <summary>True when <paramref name="partition"/> is configured for DB sync.</summary>
    public bool Includes(string partition) =>
        Partitions.Any(p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Controls whether a brand-new authenticated user may self-provision their own
/// account + per-user partition through the <c>/onboarding</c> flow (open vs
/// closed registration).
/// </summary>
public sealed record OnboardingFeatureOptions
{
    /// <summary>
    /// When <c>true</c> (default — current behaviour, no regression), any newly
    /// authenticated user without an Active User node may self-onboard. When
    /// <c>false</c>, registration is closed: self-onboarding is refused with a
    /// "contact your administrator" message instead of materialising the user.
    ///
    /// <para><b>First-user bootstrap exception:</b> a brand-new deployment with
    /// ZERO existing User nodes always lets the very first user onboard (and
    /// become platform admin) even when this flag is <c>false</c> — otherwise the
    /// platform would lock out with no administrator. The exception reuses the
    /// existing "no existing User nodes" detection in the onboarding flow.</para>
    /// </summary>
    public bool AllowSelfOnboarding { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, onboarding is allowed ONLY for an email that has an outstanding
    /// (<see cref="MeshWeaver.Mesh.InvitationStatus.Pending"/>) <see cref="MeshWeaver.Mesh.Invitation"/>.
    /// An admin issues invitations from the "Invitations" settings tab; the invited person is
    /// emailed and, when they sign in via the IdP, the verified email is matched against an
    /// outstanding invitation. Any non-invited email is refused at the onboarding gate.
    ///
    /// <para>Independent of <see cref="AllowSelfOnboarding"/>: when invitation-only is on it is the
    /// binding gate (an invited email onboards even if self-onboarding is also disabled). The
    /// <b>first-user bootstrap exception</b> still applies — a brand-new deployment with zero
    /// existing User nodes always lets the very first user onboard so the platform never locks out.
    /// Default <c>false</c> preserves current behaviour.</para>
    /// </summary>
    public bool InvitationOnly { get; init; } = false;
}

/// <summary>
/// Selects the Orleans cluster-membership provider. Bound from
/// <c>Features:Orleans:Clustering</c>. Values:
/// <list type="bullet">
///   <item><c>AzureTables</c> (default) — Aspire-injected Azure Table Storage membership
///     (the ACA / Marketplace path; the silo relies on the Aspire Orleans integration).</item>
///   <item><c>AdoNet</c> — PostgreSQL-backed membership on the separate <c>orleans</c> database
///     (real clustering for self-host / HA). The silo calls <c>UseAdoNetClustering</c> against the
///     Aspire-injected <c>ConnectionStrings:orleans</c>; the migration creates the membership tables.</item>
///   <item><c>Localhost</c> — single in-process silo (local dev only; never production).</item>
/// </list>
/// </summary>
public sealed record OrleansFeatureOptions
{
    public string Clustering { get; init; } = "AzureTables";
}

public sealed record AiFeatureOptions
{
    /// <summary>In-process API providers (one flag each).</summary>
    public AiProviderFeatureOptions Providers { get; init; } = new();

    /// <summary>Co-hosted CLI providers (Claude Code, GitHub Copilot).</summary>
    public AiCliFeatureOptions Clis { get; init; } = new();
}

public sealed record AiProviderFeatureOptions
{
    public bool Anthropic { get; init; } = true;
    public bool AzureFoundry { get; init; } = true;
    public bool AzureOpenAI { get; init; } = true;
    public bool OpenAI { get; init; } = true;

    /// <summary>
    /// The generic OpenAI-compatible custom-URL provider — the "type" a user picks in
    /// Settings → Language Models to bring any OpenAI-wire endpoint (OpenRouter, Groq,
    /// Together, a local vLLM, …) by base URL + key. No system default; always user-supplied.
    /// </summary>
    public bool OpenAICompatible { get; init; } = true;

    /// <summary>
    /// OpenRouter — the OpenAI-wire model gateway at <c>https://openrouter.ai/api/v1</c>.
    /// Ships a system-default endpoint but no model ids (auto-listed/added later as mesh
    /// data). Requires an API key. Rides the OpenAI-compatible factory.
    /// </summary>
    public bool OpenRouter { get; init; } = true;

    public bool HasAny => Anthropic || AzureFoundry || AzureOpenAI || OpenAI || OpenAICompatible || OpenRouter;
}

public sealed record AiCliFeatureOptions
{
    public bool ClaudeCode { get; init; } = true;
    public bool Copilot { get; init; } = true;

    public bool HasAny => ClaudeCode || Copilot;
}
