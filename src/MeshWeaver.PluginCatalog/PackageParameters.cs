using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What KIND of thing a declared package parameter is — which decides where the environment supplies
/// it from. The three kinds are the three shapes an environment's service graph actually emits; there
/// is deliberately no "any config key" kind, because the point is that a package names a SERVICE, not
/// a config path of its own invention.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageParameterKind>))]
public enum PackageParameterKind
{
    /// <summary>A connection string — <c>ConnectionStrings:{service}</c>. This is exactly what Aspire's
    /// <c>WithReference(db)</c> injects (<c>ConnectionStrings__memex</c>) and what the Helm chart /
    /// Key Vault CSI mount supply on AKS.</summary>
    ConnectionString,

    /// <summary>Another service's endpoint — <c>Services:{service}:{scheme}:{index}</c>, the
    /// <c>Microsoft.Extensions.ServiceDiscovery</c> shape Aspire's <c>WithReference(project)</c>
    /// injects as <c>services__{service}__https__0</c>. <c>https</c>, then <c>http</c>, then
    /// <c>default</c>, then a bare <c>Services:{service}</c> leaf.</summary>
    Endpoint,

    /// <summary>A plain provisioned value — <c>Parameters:{service}</c>, the shape an Aspire
    /// <c>AddParameter</c> (or a plain env var) supplies. Use it for an API key or a tenant id that
    /// is neither a connection string nor an endpoint.</summary>
    Value,
}

/// <summary>
/// One parameter a package REQUIRES its environment to supply — a connection string, another
/// service's endpoint, a provisioned value.
///
/// <para>🚨 The point is that the package declares a NEED and the ENVIRONMENT decides where it comes
/// from (an Aspire resource reference, a Helm value, a Key Vault secret), instead of every package
/// inventing its own config lookup. The live counter-example: the Cosmos and Snowflake storage
/// backends both document a connection-string convention of their own
/// (<c>ConnectionStrings:memexcosmos</c>) that nothing reads, so their only actual channel is
/// <c>Graph:Storage:ConnectionString</c> — two inventions, one of them dead.</para>
///
/// <para>Authored on the package root's own content (<c>"parameters": [ … ]</c> inside the node-repo
/// <c>index.json</c>'s <c>content</c>, or on a <c>package.json</c> manifest) and read off it while
/// listing, exactly like <c>preInstalled</c> / <c>publicSegments</c> / <c>module</c>.</para>
/// </summary>
public sealed record PackageParameter
{
    /// <summary>The parameter's name — and, unless <see cref="Service"/> says otherwise, the name of
    /// the service/connection/value it resolves against.</summary>
    public string Name { get; init; } = "";

    /// <summary>Where the environment supplies it from. Defaults to
    /// <see cref="PackageParameterKind.ConnectionString"/>, the common case.</summary>
    public PackageParameterKind Kind { get; init; } = PackageParameterKind.ConnectionString;

    /// <summary>The service-graph name to resolve against, when it differs from <see cref="Name"/>
    /// (a package's own name for a thing need not be the deployment's). Null = use <see cref="Name"/>.</summary>
    public string? Service { get; init; }

    /// <summary>What the package needs it FOR. Shown verbatim in the refusal, so an operator reading
    /// the log knows what they are provisioning and why.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the package still works without it. <b>Default <c>false</c> — required</b>, so the
    /// gate fails closed for anything an author did not deliberately mark optional. The CLR default
    /// also makes the flag round-trip loss-free under default-suppressing serialization (the
    /// declared-<c>true</c> bool trap already diagnosed on this codebase).
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>The name resolved against the environment's service graph.</summary>
    [JsonIgnore]
    public string Reference => string.IsNullOrWhiteSpace(Service) ? Name : Service!.Trim();

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Kind})";
}

/// <summary>
/// Resolves a package's declared <see cref="PackageParameter"/>s against the environment's service
/// graph, and REFUSES an install whose required parameters this environment does not supply.
///
/// <para>🚨 Fail closed, and name exactly what to provision. A half-configured install and a silent
/// skip are both worse than a loud refusal: the skip is the trapdoor shape AGENTS.md forbids in
/// gates, and the half-install leaves content that errors at use with nothing pointing back at the
/// missing key. The refusal message therefore carries the ENV-VAR form of every missing key, which is
/// what an operator actually pastes into a values file.</para>
/// </summary>
public static class PackageParameters
{
    /// <summary>The endpoint schemes probed, in order, for <see cref="PackageParameterKind.Endpoint"/>.</summary>
    private static readonly string[] EndpointSchemes = ["https", "http", "default"];

    /// <summary>A configured value, or null when the environment set the key to nothing. A blank
    /// value is NOT a supplied parameter — the chart renders empty strings for unset keys, and
    /// treating one as satisfied is exactly the half-configured install the gate exists to stop.</summary>
    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The configuration key <paramref name="parameter"/> resolves from — the canonical one for its
    /// kind (the endpoint kind probes several; this names the one an operator should set).
    /// </summary>
    /// <param name="parameter">The declared parameter.</param>
    /// <returns>A colon-delimited configuration path.</returns>
    public static string ConfigKey(PackageParameter parameter) => parameter.Kind switch
    {
        PackageParameterKind.ConnectionString => $"ConnectionStrings:{parameter.Reference}",
        PackageParameterKind.Endpoint => $"Services:{parameter.Reference}:https:0",
        _ => $"Parameters:{parameter.Reference}",
    };

    /// <summary>
    /// The ENVIRONMENT-VARIABLE form of <see cref="ConfigKey"/> — what actually reaches a container
    /// (<c>ConnectionStrings__warehouse</c>, <c>Services__crm__https__0</c>), and therefore what a
    /// refusal must print.
    /// </summary>
    /// <param name="parameter">The declared parameter.</param>
    /// <returns>The double-underscore env-var name.</returns>
    public static string EnvironmentVariable(PackageParameter parameter) =>
        ConfigKey(parameter).Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The value this environment supplies for <paramref name="parameter"/>, or <c>null</c> when it
    /// supplies none. Pure — no I/O, no mesh — so the whole decision is unit-testable.
    /// </summary>
    /// <param name="configuration">The environment's configuration; null supplies nothing.</param>
    /// <param name="parameter">The declared parameter.</param>
    /// <returns>The resolved value, or null.</returns>
    public static string? Resolve(IConfiguration? configuration, PackageParameter parameter)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(parameter.Reference))
            return null;
        if (parameter.Kind != PackageParameterKind.Endpoint)
            return NonEmpty(configuration[ConfigKey(parameter)]);

        // Service discovery publishes one section per scheme, each an ARRAY of endpoints; a
        // deployment that publishes a single URL as a leaf is honoured too.
        foreach (var scheme in EndpointSchemes)
        {
            var section = configuration.GetSection($"Services:{parameter.Reference}:{scheme}");
            // ORDERED by the array index, not by provider enumeration order: a service publishing
            // several endpoints must resolve to the same one on every read, and `Services__x__https__0`
            // is the one the discovery convention treats as primary.
            var first = section.GetChildren()
                .OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => NonEmpty(c.Value))
                .FirstOrDefault(v => v is not null);
            if (first is not null)
                return first;
            if (NonEmpty(section.Value) is { } leaf)
                return leaf;
        }
        return NonEmpty(configuration[$"Services:{parameter.Reference}"]);
    }

    /// <summary>
    /// The REQUIRED parameters of <paramref name="manifest"/> this environment does not supply, in
    /// declaration order. Empty = the package may install. Pure.
    /// </summary>
    /// <param name="configuration">The environment's configuration; null supplies nothing.</param>
    /// <param name="manifest">The package manifest (null declares nothing).</param>
    /// <returns>The unsatisfied required parameters.</returns>
    public static ImmutableList<PackageParameter> Missing(
        IConfiguration? configuration, PackageManifest? manifest) =>
        (manifest?.Parameters ?? [])
            .Where(p => !p.Optional && !string.IsNullOrWhiteSpace(p.Name))
            .Where(p => Resolve(configuration, p) is null)
            .ToImmutableList();

    /// <summary>
    /// The refusal text: what is missing, what it is for, and the exact env var to provision. One
    /// line per parameter, because an operator fixes them one line at a time.
    /// </summary>
    /// <param name="manifest">The package that declared them.</param>
    /// <param name="missing">The unsatisfied required parameters.</param>
    /// <returns>The speaking refusal message.</returns>
    public static string Explain(
        PackageManifest manifest, IReadOnlyCollection<PackageParameter> missing) =>
        $"Package '{manifest.Id}' requires {missing.Count} parameter(s) this environment does not "
        + "supply:"
        + string.Concat(missing.Select(p =>
            $"\n  {p.Name} ({p.Kind}){(string.IsNullOrWhiteSpace(p.Description) ? "" : $" — {p.Description}")}"
            + $"\n    provision: {EnvironmentVariable(p)}"))
        + "\nNothing was installed.";

    /// <summary>
    /// The GATE. Emits once and completes when every required parameter resolves; faults with a
    /// <see cref="PackageParameterException"/> — naming each missing parameter and its env var —
    /// when one does not.
    ///
    /// <para>Sits on <c>CatalogLayoutAreas.InstallOrUpdate</c>, the single orchestrator every install
    /// lane funnels through (the boot default install, the Store's Provision click, the auto-update
    /// reconciler), beside the entitlement gate — so no lane can bypass it and no lane needs its own
    /// copy.</para>
    /// </summary>
    /// <param name="hub">The hub whose service provider carries the environment's configuration.</param>
    /// <param name="manifest">The package about to be installed.</param>
    /// <param name="logger">Receives the refusal at Error before it faults.</param>
    /// <returns>A cold observable that emits once, or faults.</returns>
    public static IObservable<Unit> Require(
        IMessageHub hub, PackageManifest manifest, ILogger? logger = null) =>
        Observable.Defer(() =>
        {
            var missing = Missing(hub.ServiceProvider.GetService<IConfiguration>(), manifest);
            if (missing.Count == 0)
                return Observable.Return(Unit.Default);
            var explanation = Explain(manifest, missing);
            logger?.LogError("[PackageParameters] {Explanation}", explanation);
            return Observable.Throw<Unit>(new PackageParameterException(explanation));
        });
}

/// <summary>
/// An install refused because the environment does not supply a parameter the package declares as
/// required. Deliberately its own type, like <see cref="PackageAuthorizationException"/>: a refusal
/// is not a transient failure to retry or fall back from.
/// </summary>
public sealed class PackageParameterException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="PackageParameterException"/> class.</summary>
    public PackageParameterException()
    {
    }

    /// <summary>Initializes a new instance with the refusal <paramref name="message"/>.</summary>
    /// <param name="message">The speaking refusal reason, naming what to provision.</param>
    public PackageParameterException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and an inner cause.</summary>
    /// <param name="message">The refusal reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public PackageParameterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
