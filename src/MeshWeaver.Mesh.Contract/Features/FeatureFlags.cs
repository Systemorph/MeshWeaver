using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MeshWeaver.Mesh.Features;

/// <summary>
/// One declared feature of a deployment: a named switch an environment turns on or off, optionally
/// carrying the packages that environment ALWAYS has.
///
/// <para>Declared under <c>Features:Flags:{name}</c> — the same <c>Features</c> section an operator
/// already edits per environment (<c>Features:Ai:Providers:OpenAI</c>,
/// <c>Features:StaticRepoSync:Partitions</c>, …). The difference is that those are fixed fields on a
/// C# record, so a deployment can only toggle what the platform already knows about; a flag here is
/// declared by the ENVIRONMENT, which is what "pre-install from any repo, per environment" needs.
/// </para>
///
/// <para>Environment-variable form, which is what actually reaches AKS and Container Apps:
/// <code>
/// Features__Flags__store__Packages__0=Plugins/Store
/// Features__Flags__education__Enabled=false
/// Features__Flags__betaChat=true
/// </code>
/// </para>
/// </summary>
/// <param name="Name">The flag's name, as declared (the config key).</param>
/// <param name="Enabled">Whether this environment has the feature switched on.</param>
/// <param name="Packages">
/// The packages this feature covers, in the <c>Source/Package</c> notation the plugin grants and
/// <c>PluginCatalog:InstallByDefault</c> already use (<c>Plugins/*</c>,
/// <c>Reinsurance/UWDeepfield</c>). Empty = a plain named boolean with no content behind it.
///
/// <para>🚨 Which DIRECTION they act in is decided by <paramref name="Enabled"/>: an enabled flag
/// INCLUDES its packages, a declared-but-DISABLED flag EXCLUDES them. That is what lets one shared
/// declaration serve every environment and a single line express the difference — "all of Plugins,
/// without the games" is <c>plugins</c> on and <c>games</c> off, not a re-typed allow-list.</para>
/// </param>
/// <param name="Description">Optional operator note, shown on the admin surface.</param>
public sealed record FeatureFlag(
    string Name, bool Enabled, ImmutableList<string> Packages, string? Description);

/// <summary>
/// The deployment's declared features, READ REACTIVELY.
///
/// <para>🚨 There is deliberately no synchronous <c>bool IsEnabled(string)</c>. Configuration is
/// layered and reloadable (<c>MemexConfiguration</c> opens its JSON with <c>reloadOnChange: true</c>),
/// so a value sampled once is stale the moment a provider reloads — and everything on this platform
/// binds state as <see cref="IObservable{T}"/> anyway. A view binds <see cref="All"/> or
/// <see cref="IsEnabled"/> directly and re-renders when the answer changes.</para>
/// </summary>
public interface IFeatureFlags
{
    /// <summary>Every declared flag by name (case-insensitive, as configuration keys are), pushed
    /// again whenever the configuration reloads. Replays the current value to a late subscriber.</summary>
    IObservable<ImmutableSortedDictionary<string, FeatureFlag>> All { get; }

    /// <summary>Whether <paramref name="name"/> is declared AND on. An UNDECLARED flag is
    /// <c>false</c> — a deployment opts in by declaring it.</summary>
    /// <param name="name">The flag name (matched case-insensitively).</param>
    IObservable<bool> IsEnabled(string name);

    /// <summary>The declared flag, or <c>null</c> when this environment does not declare it.</summary>
    /// <param name="name">The flag name (matched case-insensitively).</param>
    IObservable<FeatureFlag?> Get(string name);

    /// <summary>
    /// What this environment's flags compose to — the packages it INCLUDES and the packages it
    /// EXCLUDES, each carrying the flag that decided it. Re-emitted on every configuration reload.
    /// </summary>
    IObservable<FeatureComposition> Composition { get; }
}

/// <summary>One package a feature flag covers, and the flag it came from.</summary>
/// <param name="Flag">The declaring flag's name — what a log line or a refusal must name to be actionable.</param>
/// <param name="Package">The <c>Source/Package</c> pattern, verbatim as declared.</param>
public readonly record struct FeaturePackage(string Flag, string Package);

/// <summary>
/// What a deployment's feature flags compose to.
///
/// <para><b>The two directions, and why both exist.</b> An allow-list and an exclusion have
/// different silent failure modes: an allow-list silently OMITS a package newly added to a repo
/// (it never reaches the portal and nobody notices), an exclusion silently INCLUDES one. Neither is
/// right for every case, so a flag expresses whichever the operator means — the packages of an
/// ENABLED flag are included, the packages of a declared-but-DISABLED flag are excluded — and the
/// admin surface names exactly which flag decided each package, so neither direction is silent in
/// practice.</para>
///
/// <para><b>Exclusion wins.</b> A package named by a disabled flag is excluded even if an enabled
/// flag's wildcard (or the platform's own <c>preInstalled</c> baseline) would otherwise select it:
/// "this environment does not have that" is an explicit statement, and subtraction is the only
/// reading under which it can be one.</para>
/// </summary>
/// <param name="Included">Packages the enabled flags select, ordered by flag then package.</param>
/// <param name="Excluded">Packages the declared-but-disabled flags remove, same ordering.</param>
public sealed record FeatureComposition(
    ImmutableList<FeaturePackage> Included, ImmutableList<FeaturePackage> Excluded)
{
    /// <summary>A composition that neither includes nor excludes anything.</summary>
    public static FeatureComposition Empty { get; } = new([], []);
}

/// <summary>
/// The <see cref="IFeatureFlags"/> implementation over <see cref="IConfiguration"/>.
///
/// <para>A <b>mesh-scoped singleton</b> registered by <c>MeshBuilder</c> — it holds its state on an
/// INSTANCE field and dies with the mesh, so nothing bleeds between tests or between users
/// (Doc/Architecture/NoStaticState). Reloads arrive as a PUSH from the configuration provider
/// (<see cref="ChangeToken.OnChange{TState}"/>): no timer, no poller, no watchdog.</para>
/// </summary>
public sealed class ConfigurationFeatureFlags : IFeatureFlags, IDisposable
{
    /// <summary>The configuration section declaring the flags.</summary>
    public const string SectionName = "Features:Flags";

    private readonly IConfiguration? configuration;
    private readonly ILogger<ConfigurationFeatureFlags>? logger;
    private readonly BehaviorSubject<ImmutableSortedDictionary<string, FeatureFlag>> flags;
    private readonly IDisposable? reloadSubscription;

    /// <summary>Initializes the reader and takes the first reading.</summary>
    /// <param name="configuration">The host configuration; null (a mesh built without one) declares no flags.</param>
    /// <param name="logger">Receives the malformed-value warnings.</param>
    public ConfigurationFeatureFlags(
        IConfiguration? configuration = null, ILogger<ConfigurationFeatureFlags>? logger = null)
    {
        this.configuration = configuration;
        this.logger = logger;
        flags = new BehaviorSubject<ImmutableSortedDictionary<string, FeatureFlag>>(Read());
        // A push from the provider — the reload token is re-registered by OnChange itself, so this
        // survives every reload rather than firing once.
        reloadSubscription = configuration is null
            ? null
            : ChangeToken.OnChange(configuration.GetReloadToken, () => flags.OnNext(Read()));
    }

    /// <inheritdoc />
    /// <remarks><c>AsObservable</c> so a consumer cannot cast the handle back to the subject and
    /// push a composition nobody configured.</remarks>
    public IObservable<ImmutableSortedDictionary<string, FeatureFlag>> All => flags.AsObservable();

    /// <inheritdoc />
    public IObservable<bool> IsEnabled(string name) =>
        All.Select(all => all.TryGetValue(name, out var flag) && flag.Enabled).DistinctUntilChanged();

    /// <inheritdoc />
    public IObservable<FeatureFlag?> Get(string name) =>
        All.Select(all => all.TryGetValue(name, out var flag) ? flag : null).DistinctUntilChanged();

    /// <inheritdoc />
    public IObservable<FeatureComposition> Composition =>
        All.Select(Compose).DistinctUntilChanged();

    /// <summary>
    /// Folds the declared flags into the environment's composition: enabled flags contribute
    /// INCLUSIONS, declared-but-disabled flags contribute EXCLUSIONS. Pure, so the whole rule is
    /// unit-testable without a mesh.
    /// </summary>
    /// <param name="all">The declared flags.</param>
    /// <returns>What this environment includes and excludes.</returns>
    public static FeatureComposition Compose(ImmutableSortedDictionary<string, FeatureFlag> all)
    {
        ImmutableList<FeaturePackage> Side(bool enabled) =>
            all.Values
                .Where(f => f.Enabled == enabled)
                .SelectMany(f => f.Packages.Select(p => new FeaturePackage(f.Name, p)))
                .Distinct()
                .OrderBy(p => p.Flag, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Package, StringComparer.Ordinal)
                .ToImmutableList();
        return new FeatureComposition(Side(enabled: true), Side(enabled: false));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        reloadSubscription?.Dispose();
        flags.Dispose();
    }

    private ImmutableSortedDictionary<string, FeatureFlag> Read()
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, FeatureFlag>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration?.GetSection(SectionName).GetChildren() ?? [])
        {
            var name = child.Key;
            // Two authored shapes. The LEAF form (`Features__Flags__betaChat=true`) is a plain named
            // boolean — the cheapest thing to write in a values file, and the whole point of a
            // dynamic flag. The OBJECT form adds the packages and the note.
            var flag = child.Value is not null
                ? new FeatureFlag(name, Enabled(name, child.Value), [], null)
                : new FeatureFlag(
                    name,
                    Enabled(name, child["Enabled"]),
                    child.GetSection("Packages").GetChildren()
                        .Select(p => p.Value?.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Select(p => p!)
                        .ToImmutableList(),
                    child["Description"]);
            builder[name] = flag;
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// Reads a flag's <c>Enabled</c> value. ABSENT (or blank) means <b>on</b> — declaring the flag in
    /// an environment's values IS the opt-in, and the separate key exists so a shared base file can
    /// declare a feature that one environment then switches off without deleting the declaration.
    /// A value that is present and NOT a boolean is a config error: it reads as OFF and is named at
    /// Warning, because a non-boolean must never be taken as consent (the same rule
    /// <c>PackageSources.Flag</c> applies to the auto-sync switches).
    /// </summary>
    private bool Enabled(string name, string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;
        if (bool.TryParse(trimmed, out var parsed))
            return parsed;
        logger?.LogWarning(
            "Feature flag '{Flag}' has a non-boolean Enabled value '{Value}' — reading it as OFF. "
            + "Set {Key}:{Flag}:Enabled to true or false.", name, trimmed, SectionName, name);
        return false;
    }
}
