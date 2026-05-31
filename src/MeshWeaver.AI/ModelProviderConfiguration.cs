using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// Content shape for <c>nodeType:ModelProvider</c> mesh nodes — one node per
/// (user, provider) pair holding the credentials a chat-client factory uses
/// to authenticate against the provider's API.
///
/// <para>Two layers populate this NodeType:</para>
/// <list type="bullet">
///   <item><b>Static layer</b>: <see cref="BuiltInLanguageModelProvider"/>
///         emits one read-only <c>ModelProvider</c> per
///         <see cref="LanguageModelCatalogSource"/> at
///         <c>Model/{providerName}</c>, stamped with the values from the
///         matching <c>{section}:ApiKey</c> / <c>{section}:Endpoint</c>
///         IConfiguration entries. This preserves backward compatibility for
///         deployments that wire credentials via appsettings.</item>
///   <item><b>User layer</b>: <c>ModelProviderService</c> creates
///         user-authored <c>ModelProvider</c> nodes at
///         <c>{userId}/Model/{providerName}</c> when a user pastes their
///         personal key in the Models settings tab. The factory resolver
///         prefers these over the static layer via
///         <c>scope:selfAndAncestors</c> closest-wins.</item>
/// </list>
///
/// <para>The literal <see cref="ApiKey"/> lives on this content. RLS gates
/// read access — only the owning user (or a partition admin) can read the
/// node, so the key never reaches other tenants. Keyless providers
/// (GitHub Copilot, local Claude Code CLI) leave <see cref="ApiKey"/> null.</para>
/// </summary>
public record ModelProviderConfiguration
{
    /// <summary>
    /// Provider label — matches <see cref="IChatClientFactory.Name"/> and
    /// the <see cref="ModelDefinition.Provider"/> stamp on each child
    /// LanguageModel node. Looked up in <c>ProviderRegistry.Find</c>
    /// to pull default endpoint / default model ids.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Literal API key (or subscription token) the factory passes as
    /// <c>x-api-key</c> / <c>Authorization: Bearer</c>. Null for keyless
    /// providers (Copilot uses OAuth, ClaudeCode runs a local binary).
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Optional endpoint override. Null means "use the
    /// <c>KnownProviderProfile.DefaultEndpoint</c> from
    /// <c>ProviderRegistry</c>". A non-null value flows through to
    /// the factory unchanged.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>Human-readable display name (e.g. "Roland's personal key").</summary>
    public string? Label { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// Snapshot of the model ids the service auto-created under this provider
    /// when it was first saved. Lets the UI show a count without re-querying
    /// the LanguageModel children, and gives the service a single source of
    /// truth for cascade-delete.
    /// </summary>
    public ImmutableArray<string> Models { get; init; } = ImmutableArray<string>.Empty;
}
