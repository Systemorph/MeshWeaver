using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// A git HOSTING provider GitSync syncs with beside GitHub — the seam a module contributes a
/// hosting service through, so the platform's GitSync carries no provider's SDK or credential code
/// (Doc/Architecture/GitHubSync → "Hosting providers").
///
/// <para><b>What core keeps.</b> GitHub (REST + the git protocol) and every other plain git remote
/// stay the platform's own path. A URL that a registered provider <see cref="Claims"/> leaves that
/// path entirely: its bulk transfer runs on the provider's <see cref="RepoClient"/>
/// (<see cref="GitProviderRepoClient"/> routes it), every read and push authenticates with the
/// provider's own instance identity (<see cref="GetAccessToken"/>) — never a user's GitHub
/// credential, which means nothing there — and the operations it does not offer are refused by
/// name (<see cref="Refuse"/>). The import, export and two-way rules above the client are the same
/// for every provider by construction.</para>
///
/// <para><b>Registration.</b> <see cref="GitHostingProviderRegistration.AddGitHostingProvider"/>; the
/// set is read by <see cref="GitHostingProviders"/>, which refuses two providers of one
/// <see cref="Kind"/> and a URL claimed by two providers by name, never by picking one.</para>
/// </summary>
public interface IGitHostingProvider
{
    /// <summary>The provider's kind — an OPEN vocabulary (policy
    /// <c>open-vocabulary-string-constants</c>): the platform's own values are
    /// <see cref="GitRepositoryProvider.GitHub"/> and <see cref="GitRepositoryProvider.Git"/>, which a
    /// contributed provider may not take; every other value belongs to the module that declares it.</summary>
    string Kind { get; }

    /// <summary>True when <paramref name="repositoryUrl"/> names a repository this provider hosts.</summary>
    /// <param name="repositoryUrl">A non-blank repository URL.</param>
    bool Claims(string repositoryUrl);

    /// <summary>The client every operation on a claimed URL runs on.</summary>
    IGitHubRepoClient RepoClient { get; }

    /// <summary>The instance's own access token for this provider — one token for reads and pushes
    /// alike. A provider that is not configured on this instance refuses by name.</summary>
    IObservable<string> GetAccessToken();

    /// <summary>The commit author name of an export authenticated with the instance identity.</summary>
    string CommitAuthorName => "MeshWeaver";

    /// <summary>The commit author email of an export authenticated with the instance identity.</summary>
    string CommitAuthorEmail => "noreply@meshweaver.cloud";

    /// <summary>The refusal for <paramref name="operation"/> (a <see cref="GitHostingOperation"/>
    /// value) on a claimed URL, or null when the provider offers it.</summary>
    /// <param name="repositoryUrl">The claimed repository URL.</param>
    /// <param name="operation">The operation.</param>
    GitSyncRefusal? Refuse(string repositoryUrl, string operation) => null;

    /// <summary>The localization key of the settings tab's one-line provider description.</summary>
    string DisplayNameKey => "ui.gitSync.provider." + Kind;
}

/// <summary>
/// The operations beyond the sync itself that a hosting provider may not offer — an OPEN vocabulary
/// of string constants a provider answers in <see cref="IGitHostingProvider.Refuse"/>.
/// </summary>
public static class GitHostingOperation
{
    /// <summary>Any pull-request operation (draft, open, status, list, comment, merge).</summary>
    public const string PullRequest = "pullRequest";

    /// <summary>Any issue operation (list, sync, create, comment, close).</summary>
    public const string Issues = "issues";
}

/// <summary>
/// The hosting providers registered on this mesh, and the ONE place a repository URL is resolved to
/// one of them. A mesh-scoped singleton (<see cref="GitHubSyncConfiguration.AddGitHubSyncServices"/>).
///
/// <para>🚨 <b>Loud, never a silent choice.</b> Two providers declaring one <see cref="IGitHostingProvider.Kind"/>,
/// or a provider taking the platform's own <see cref="GitRepositoryProvider.GitHub"/> /
/// <see cref="GitRepositoryProvider.Git"/>, refuse the registry when it is built; a URL two providers
/// both claim is refused by name (<c>gitsync.provider.ambiguous</c>) wherever it is resolved. A URL no
/// provider claims is the platform's own GitHub / git path — that is the declared meaning of
/// <see cref="GitRepositoryProvider.Git"/>, not a fallback.</para>
///
/// <para><b>Built-in providers</b> are the platform's transitional own: a CONTRIBUTED provider of the
/// same kind replaces one, so a module that takes a provider over can land before the platform
/// drops its copy (policy <c>dependents-verify-on-their-side</c>: the deleting half lands last).</para>
/// </summary>
public sealed class GitHostingProviders
{
    private readonly ImmutableArray<IGitHostingProvider> providers;

    /// <summary>Builds the registry.</summary>
    /// <param name="contributed">The providers registered through
    /// <see cref="GitHostingProviderRegistration.AddGitHostingProvider"/>.</param>
    /// <param name="builtIn">The platform's own providers; each yields to a contributed provider of
    /// the same kind.</param>
    /// <param name="logger">Optional logger.</param>
    /// <exception cref="InvalidOperationException">Two contributed providers share a kind, or a
    /// provider takes a platform kind.</exception>
    public GitHostingProviders(
        IEnumerable<IGitHostingProvider> contributed,
        IEnumerable<IGitHostingProvider>? builtIn = null,
        ILogger<GitHostingProviders>? logger = null)
    {
        var own = contributed.ToImmutableArray();
        foreach (var provider in own.Concat(builtIn ?? []))
            if (string.IsNullOrWhiteSpace(provider.Kind)
                || provider.Kind is GitRepositoryProvider.GitHub or GitRepositoryProvider.Git)
                throw new InvalidOperationException(
                    $"Git hosting provider {provider.GetType().FullName} declares the kind '{provider.Kind}', " +
                    $"which is the platform's own (or blank). Declare a kind of the module's own.");
        foreach (var group in own.GroupBy(p => p.Kind, StringComparer.Ordinal).Where(g => g.Count() > 1))
            throw new InvalidOperationException(
                $"Git hosting provider kind '{group.Key}' is registered more than once: " +
                string.Join(", ", group.Select(p => p.GetType().FullName)) + ". Register each kind once.");
        var kinds = own.Select(p => p.Kind).ToImmutableHashSet(StringComparer.Ordinal);
        var kept = (builtIn ?? []).Where(p => !kinds.Contains(p.Kind)).ToImmutableArray();
        foreach (var replaced in (builtIn ?? []).Where(p => kinds.Contains(p.Kind)))
            logger?.LogInformation(
                "Git hosting provider '{Kind}': the contributed {Contributed} replaces the platform's built-in {BuiltIn}.",
                replaced.Kind, own.First(p => p.Kind == replaced.Kind).GetType().FullName, replaced.GetType().FullName);
        providers = own.AddRange(kept);
    }

    /// <summary>A registry with no hosting provider: every URL is the platform's own path.</summary>
    public static GitHostingProviders None { get; } = new([]);

    /// <summary>Every registered provider (contributed first, then the built-ins they left).</summary>
    public IReadOnlyList<IGitHostingProvider> All => providers;

    /// <summary>
    /// The provider that hosts <paramref name="repositoryUrl"/>, or null for the platform's own
    /// GitHub / git path (also for a null or blank URL).
    /// </summary>
    /// <exception cref="GitSyncRefusalException">Two providers claim the URL
    /// (<c>gitsync.provider.ambiguous</c>).</exception>
    public IGitHostingProvider? For(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl) || providers.IsEmpty)
            return null;
        IGitHostingProvider? match = null;
        foreach (var provider in providers)
        {
            if (!provider.Claims(repositoryUrl))
                continue;
            if (match is not null)
                throw GitSyncRefusal.Of("gitsync.provider.ambiguous",
                    ("url", repositoryUrl), ("first", match.Kind), ("second", provider.Kind)).ToException();
            match = provider;
        }
        return match;
    }

    /// <summary>
    /// The provider kind of <paramref name="repositoryUrl"/>: a claiming provider's
    /// <see cref="IGitHostingProvider.Kind"/>, else <see cref="GitRepositoryProvider.GitHub"/> for a
    /// <c>github.com</c> URL, else <see cref="GitRepositoryProvider.Git"/>.
    /// </summary>
    public string Classify(string? repositoryUrl)
        => For(repositoryUrl)?.Kind
           ?? (Uri.TryCreate(repositoryUrl?.Trim(), UriKind.Absolute, out var uri)
               && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                   ? GitRepositoryProvider.GitHub
                   : GitRepositoryProvider.Git);

    /// <summary>The refusal for <paramref name="operation"/> (a <see cref="GitHostingOperation"/>
    /// value) on <paramref name="repositoryUrl"/>: the claiming provider's answer, null on the
    /// platform's own path.</summary>
    public GitSyncRefusal? Refuse(string? repositoryUrl, string operation)
        => For(repositoryUrl) is { } provider ? provider.Refuse(repositoryUrl!, operation) : null;
}

/// <summary>Registration of a <see cref="IGitHostingProvider"/>.</summary>
public static class GitHostingProviderRegistration
{
    /// <summary>
    /// Registers a git hosting provider. Call it beside
    /// <see cref="GitHubSyncConfiguration.AddGitHubSyncServices"/> (in either order); one provider per
    /// <see cref="IGitHostingProvider.Kind"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="factory">Builds the provider from the container.</param>
    public static IServiceCollection AddGitHostingProvider(
        this IServiceCollection services, Func<IServiceProvider, IGitHostingProvider> factory)
    {
        services.AddSingleton(factory);
        return services;
    }
}

/// <summary>
/// The sync DIRECTION rules — identical for every provider, GitHub and every hosting provider alike.
/// <see cref="GitHubSyncService.SyncToGitHub"/> runs <see cref="ExportGuard"/> and
/// <see cref="GitHubSyncService.ReimportAtCommit"/> runs <see cref="ReimportGuard"/>.
///
/// <para>Both keep their shipped contract — a plain <see cref="InvalidOperationException"/> with the
/// exact English message (<see cref="GitSyncRefusal.Legacy"/>) — because callers and the dependent
/// suites assert on that type and text, while every rendered surface resolves the key.</para>
/// </summary>
public static class SyncDirectionGuards
{
    /// <summary>The EXPORT guard: the import-only direction rule.</summary>
    /// <param name="config">The sync source.</param>
    public static GitSyncRefusal? ExportGuard(GitHubSyncConfig config)
        => config.Direction == SyncDirection.ImportOnly
            ? GitSyncRefusal.Legacy("activity.gitsync.importOnlyNoExport",
                $"This sync source is import-only (repo → mesh): exporting to {config.RepositoryUrl} is not allowed. " +
                "Change the source's Sync direction to Bidirectional or Export-only to commit.",
                ("url", config.RepositoryUrl ?? ""))
            : null;

    /// <summary>The RE-IMPORT guard: the export-only direction rule.</summary>
    /// <param name="config">The sync source.</param>
    public static GitSyncRefusal? ReimportGuard(GitHubSyncConfig config)
        => config.Direction == SyncDirection.ExportOnly
            ? GitSyncRefusal.Legacy("activity.gitsync.exportOnlyNoImport",
                $"This sync source is export-only (mesh → repo): importing from {config.RepositoryUrl} is not allowed. " +
                "Change the source's Sync direction to Bidirectional or Import-only to re-import.",
                ("url", config.RepositoryUrl ?? ""))
            : null;
}
