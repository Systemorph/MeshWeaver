using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Embeddings;

/// <summary>
/// The deployment's embedding decision, taken once at composition and reported once at startup.
///
/// <para>🚨 Why this exists at all. <see cref="IEmbeddingProvider"/> is genuinely OPTIONAL — with no
/// <c>Embedding:Endpoint</c> configured there is nothing to register, semantic search degrades to
/// the ILIKE substring scan and the content-indexing pipeline resolves its inert stand-ins. That is
/// a legitimate deployment, so a missing provider must NOT fail loudly. But it was also taken
/// SILENTLY: <c>TryAddEmbeddingProvider</c> returned a bool nobody read, and nothing anywhere said
/// which way it went. An operator looking at plainly-lexical search results could not tell whether
/// embeddings were never configured, configured with a key the cloud backend rejected, or
/// configured against a module that is not landed — three different fixes behind one identical
/// symptom, which is how issue #1642 was triaged from a stack trace instead of a log line.</para>
///
/// <para>A capability that degrades silently is indistinguishable from one that is broken. This
/// record is the decision made explicit, resolvable from DI, and written to the log exactly once
/// per process by <see cref="EmbeddingCapabilityReporter"/>.</para>
/// </summary>
public sealed record EmbeddingCapability
{
    /// <summary>Whether an <see cref="IEmbeddingProvider"/> was registered.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>The selected backend (<c>Ollama</c> / <c>OpenAICompatible</c> / <c>AzureFoundry</c>).</summary>
    public string? Provider { get; init; }

    /// <summary>The configured endpoint, when there is one.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The embedding model / deployment name.</summary>
    public string? Model { get; init; }

    /// <summary>The vector dimensionality the storage schema is sized for.</summary>
    public int Dimensions { get; init; }

    /// <summary>
    /// Why embeddings are off — naming the exact configuration key to set. <c>null</c> when
    /// <see cref="IsEnabled"/>.
    /// </summary>
    public string? DisabledReason { get; init; }

    /// <summary>The config-section name every reason below refers to.</summary>
    private const string Section = "Embedding";

    /// <summary>
    /// Describes what the host did about embeddings.
    ///
    /// <para><paramref name="isRegistered"/> — whether
    /// <see cref="EmbeddingExtensions.CreateEmbeddingProvider"/> actually produced a provider — is
    /// the AUTHORITY for <see cref="IsEnabled"/>, deliberately. Re-deriving "on or off" from the
    /// options here would be a second copy of that method's branch logic, free to drift from it and
    /// then to report a capability the host does not have; the reason below explains a decision
    /// already taken, it never takes one.</para>
    /// </summary>
    /// <param name="options">The bound <c>Embedding</c> configuration section.</param>
    /// <param name="isRegistered">Whether a provider was actually created and registered.</param>
    /// <returns>The decision.</returns>
    public static EmbeddingCapability From(EmbeddingOptions options, bool isRegistered)
    {
        var configured = options.Provider?.Trim();
        var isLocal = configured is not null
                      && (configured.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                          || configured.Equals("openaicompatible", StringComparison.OrdinalIgnoreCase));
        var name = isLocal ? configured! : "AzureFoundry";

        return new EmbeddingCapability
        {
            IsEnabled = isRegistered,
            Provider = name,
            Endpoint = options.Endpoint,
            Model = options.Model,
            Dimensions = options.Dimensions,
            DisabledReason = isRegistered ? null : Reason(options, name, isLocal),
        };
    }

    /// <summary>
    /// Why no provider was created, naming the configuration key that would create one.
    /// </summary>
    /// <param name="options">The bound configuration section.</param>
    /// <param name="name">The resolved backend name.</param>
    /// <param name="isLocal">Whether the backend is the keyless on-host one.</param>
    /// <returns>The reason.</returns>
    private static string Reason(EmbeddingOptions options, string name, bool isLocal)
    {
        if (string.IsNullOrEmpty(options.Endpoint))
            return $"no {Section}:Endpoint is configured";
        if (!isLocal && string.IsNullOrEmpty(options.ApiKey))
            return $"the {name} backend needs {Section}:ApiKey and none is configured "
                   + $"(set {Section}:Provider=Ollama for a keyless on-host endpoint)";
        return $"{Section}:Endpoint is set but the {name} backend produced no provider";
    }

    /// <summary>
    /// One sentence an operator can act on, in both directions. Never carries
    /// <see cref="EmbeddingOptions.ApiKey"/> — this goes to the pod log.
    /// </summary>
    /// <returns>The human-readable decision.</returns>
    public string Describe() =>
        IsEnabled
            ? $"Semantic (vector) search ENABLED: provider={Provider}, endpoint={Endpoint}, "
              + $"model={Model}, dimensions={Dimensions}."
            : $"Semantic (vector) search DISABLED — {DisabledReason}. Free-text queries fall back "
              + "to an ILIKE substring scan and content indexing stays inert; this is a supported "
              + "configuration, not a fault.";
}

/// <summary>
/// Writes the <see cref="EmbeddingCapability"/> decision to the log once, at host start.
///
/// <para>ONE <c>Information</c> line per process — the cost model AGENTS.md describes is about
/// per-request volume, and a startup capability banner is the cheapest possible way to answer
/// "is semantic search on here?" without a debugger. It is deliberately logged for BOTH outcomes:
/// a line that appears only when the capability is on is indistinguishable from a host that never
/// reached the decision.</para>
/// </summary>
/// <param name="capability">The decision taken at composition.</param>
/// <param name="logger">The logger to report it on.</param>
public sealed class EmbeddingCapabilityReporter(
    EmbeddingCapability capability,
    ILogger<EmbeddingCapabilityReporter> logger) : IHostedService
{
    /// <summary>Reports the decision. Pure logging — no I/O, nothing to await.</summary>
    /// <param name="cancellationToken">Unused; the work is synchronous.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("{EmbeddingCapability}", capability.Describe());
        return Task.CompletedTask;
    }

    /// <summary>No-op.</summary>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
