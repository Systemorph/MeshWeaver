using System.Reactive.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The credential ONE configured git source declared for its repository
/// (<c>PluginCatalog:Sources:N:{TokenKey,InstallationOwner}</c>). Names only — where the token
/// lives, or which App installation mints it; never a token.
/// </summary>
/// <param name="SourceName">The source's configured name (<c>Sources:N:Name</c>) — what a refusal names.</param>
/// <param name="RepositoryUrl">The repository the declaration is for (<c>Sources:N:RepoPath</c>).</param>
/// <param name="TokenKey">The CONFIGURATION KEY the repository's token is read from
/// (<c>Sources:N:TokenKey</c>, e.g. <c>PluginCatalog:SourceTokens:Extensions</c>); null when the
/// source states none, EMPTY when it states the key and leaves it blank — which is refused.</param>
/// <param name="InstallationOwner">The account whose GitHub App installation mints the token
/// (<c>Sources:N:InstallationOwner</c>), or null.</param>
public sealed record GitSourceCredential(
    string SourceName, string RepositoryUrl, string? TokenKey, string? InstallationOwner)
{
    /// <summary>
    /// True when the repository is read with the instance's OWN GitHub App
    /// (<see cref="GitHubAppTokenService.GetInstanceAppInstallationToken"/>) — an instance-local
    /// package source while that App is connected, or a configured source declaring
    /// <c>Identity = instance</c> (<see cref="GitSourceCredentials.InstanceIdentity"/>).
    /// </summary>
    public bool InstanceApp { get; init; }

    /// <summary>A stated <c>Identity</c> this reader does not know (anything but
    /// <see cref="GitSourceCredentials.InstanceIdentity"/>) — refused by name, never ignored.</summary>
    public string? UnknownIdentity { get; init; }
}

/// <summary>
/// A git source DECLARED a credential and that credential could not be produced — the declared
/// configuration key is empty, the source names an App installation on an instance without the
/// App, or it names two credentials at once. The repository is then NOT read: never with the
/// instance's own App token, never anonymously. The message names the source and the key (never a
/// value).
///
/// <para>Raised server-side, usually with no viewer in scope, so it carries its
/// <see cref="GitSyncRefusal"/> — a catalog key plus named arguments — and every GitSync surface
/// that shows it renders that for its viewer (<see cref="GitSyncRefusalException.TryGet"/>).
/// <see cref="Exception.Message"/> is the English rendering.</para>
/// </summary>
public sealed class GitSourceCredentialException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="refusal"/>.</summary>
    public GitSourceCredentialException(GitSyncRefusal refusal)
        : base(refusal.Render(null))
    {
        Refusal = refusal;
        Data[GitSyncRefusalException.DataKey] = refusal;
    }

    /// <summary>The key and arguments of the refusal.</summary>
    public GitSyncRefusal Refusal { get; }
}

/// <summary>
/// Which credential reads which git repository, when a configured source says so — the ONE reading
/// of <c>PluginCatalog:Sources:N:TokenKey</c> and <c>PluginCatalog:Sources:N:InstallationOwner</c>
///.
///
/// <para>Before it, every URL source was read with the instance's ONE GitHub App installation
/// token, so an instance could not read repositories in two organisations, nor a repository its
/// App is not installed on. A source now states its own credential, by reference:</para>
/// <list type="bullet">
///   <item><c>TokenKey</c> — the configuration key holding a token for that repository (a
///   fine-grained PAT, or any token GitHub accepts over https). The VALUE arrives from the vault
///   under that key (the chart's <c>keyVaultSecrets</c> mapping); the record and the ConfigMap
///   carry the key's NAME only.</item>
///   <item><c>InstallationOwner</c> — the organisation/user whose installation of the instance's
///   App mints the token (<see cref="GitHubAppTokenService.GetInstallationToken(string?)"/>).</item>
/// </list>
///
/// <para>🚨 <b>Keyed by repository, not by source instance</b>, because a repository is read on
/// more than one route: the package source that lists and installs it
/// (<c>PackageSources</c>), and GitSync's import and branch reconcile of the Spaces installed out
/// of it (<see cref="GitHubSyncService"/>). A credential attached to only one of them reads the
/// catalog and then fails the sync, or the reverse.</para>
///
/// <para>🚨 <b>Fail closed, by name.</b> A declared credential that cannot be produced faults with
/// <see cref="GitSourceCredentialException"/>; nothing degrades to another identity.</para>
/// </summary>
public sealed class GitSourceCredentials
{
    /// <summary>The configuration list the declarations are read from.</summary>
    public const string SourcesSection = "PluginCatalog:Sources";

    /// <summary>The value of <c>PluginCatalog:Sources:N:Identity</c> that reads the repository with
    /// the instance's OWN GitHub App (<c>Doc/Architecture/InstanceRepositoryAccess</c>).</summary>
    public const string InstanceIdentity = "instance";

    private readonly IConfiguration? configuration;
    private readonly GitHubAppTokenService? appTokens;
    private readonly ILogger? logger;
    private readonly Func<string?, InstancePackageSourceContent?>? instanceSource;

    /// <summary>Initializes the reader.</summary>
    /// <param name="configuration">The instance configuration carrying <see cref="SourcesSection"/>;
    /// null declares nothing.</param>
    /// <param name="appTokens">The GitHub App identity an <c>InstallationOwner</c> mints with.</param>
    /// <param name="logger">Optional logger.</param>
    public GitSourceCredentials(
        IConfiguration? configuration,
        GitHubAppTokenService? appTokens = null,
        ILogger<GitSourceCredentials>? logger = null)
        : this(configuration, appTokens, instanceSource: null, logger)
    {
    }

    /// <summary>Initializes the reader with the instance's own package sources beside the configured ones.</summary>
    /// <param name="configuration">The instance configuration carrying <see cref="SourcesSection"/>.</param>
    /// <param name="appTokens">The GitHub App identities (the deployment's, and the instance's own).</param>
    /// <param name="instanceSource">Finds the instance-local source for a repository
    /// (<see cref="InstancePackageSourceCatalog.For"/>); null when the host keeps none.</param>
    /// <param name="logger">Optional logger.</param>
    public GitSourceCredentials(
        IConfiguration? configuration,
        GitHubAppTokenService? appTokens,
        Func<string?, InstancePackageSourceContent?>? instanceSource,
        ILogger<GitSourceCredentials>? logger = null)
    {
        this.configuration = configuration;
        this.appTokens = appTokens;
        this.instanceSource = instanceSource;
        this.logger = logger;
    }

    /// <summary>
    /// The credential the source configured for <paramref name="repositoryUrl"/> declared, or null
    /// when no source names that repository or the one that does declares none — the caller then
    /// uses its default identity exactly as before. Read from configuration on every call, so the
    /// answer follows a reloaded configuration. The first source naming the repository decides.
    /// </summary>
    public GitSourceCredential? For(string? repositoryUrl)
    {
        if (configuration is null || NormalizeRepository(repositoryUrl) is not { Length: > 0 } wanted)
            return null;
        foreach (var source in configuration.GetSection(SourcesSection).GetChildren())
        {
            if (!string.Equals(NormalizeRepository(source["RepoPath"]), wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            // A TokenKey that is PRESENT but blank is a declaration, not an absence: whoever wrote
            // the key meant this source to have its own credential, and reading "" as "none" would
            // fall back to the very identities the declaration exists to rule out. It is kept as
            // the empty key and refused by name in Token.
            var tokenKey = source["TokenKey"] is { } stated ? stated.Trim() : null;
            var owner = Blank(source["InstallationOwner"]);
            // An Identity that is stated is a declaration: `instance` asks for the instance's own App,
            // and anything else is kept VERBATIM and refused by name in Token — a misspelling must
            // never silently fall back to the deployment's App or to anonymous access.
            var identity = Blank(source["Identity"]);
            var instance = string.Equals(identity, InstanceIdentity, StringComparison.OrdinalIgnoreCase);
            if (tokenKey is null && owner is null && identity is null)
                return null;
            return new GitSourceCredential(
                Blank(source["Name"]) ?? source["RepoPath"]!.Trim(), source["RepoPath"]!.Trim(), tokenKey, owner)
            {
                InstanceApp = instance,
                UnknownIdentity = identity is not null && !instance ? identity : null,
            };
        }
        // 🚨 The instance's OWN sources, AFTER the configured ones: a repository the deployment
        // record declares is read as the record says (policy declared-not-derived). An instance
        // source is read with the instance's own App while it is connected — also on an instance
        // that has the deployment's App, because the instance App is what its administrator
        // installed on that repository. Without a connected instance App it declares nothing, and
        // the caller's default identity applies exactly as for any undeclared repository.
        if (instanceSource?.Invoke(repositoryUrl) is { } local && appTokens is { HasInstanceApp: true })
            return new GitSourceCredential(InstancePackageSources.NameOf(local), local.Repo, null, null)
            {
                InstanceApp = true,
            };
        return null;
    }

    /// <summary>
    /// The token stream for <paramref name="repositoryUrl"/> when its source declared a credential,
    /// else null. Cold: the token is resolved on each subscription, so a rotated vault value or a
    /// re-minted installation token is always current.
    /// </summary>
    public IObservable<string>? TokenFor(string? repositoryUrl) =>
        For(repositoryUrl) is { } declared ? Token(declared) : null;

    /// <summary>
    /// The token for a declared credential. Faults with <see cref="GitSourceCredentialException"/>
    /// when the declared key is empty, when the App a named installation needs is not configured,
    /// or when BOTH credentials are stated; a failed mint keeps its
    /// <see cref="GitHubAppTokenMintException"/>.
    /// </summary>
    public IObservable<string> Token(GitSourceCredential declared) => Observable.Defer(() =>
    {
        // ONE credential per source. The record refuses the pair before it is rendered, but a
        // source can also be written straight into configuration — and picking a winner there
        // would read the repository with an identity nobody can tell from the declaration.
        if (declared.TokenKey is { Length: 0 })
            return Refuse(GitSyncRefusal.Of(
                "gitsync.source.tokenKeyBlank",
                ("source", declared.SourceName), ("url", declared.RepositoryUrl)));

        if (declared.UnknownIdentity is { } unknown)
            return Refuse(GitSyncRefusal.Of(
                "gitsync.source.unknownIdentity",
                ("source", declared.SourceName), ("url", declared.RepositoryUrl), ("identity", unknown)));

        if (declared.InstanceApp && (declared.TokenKey is not null || declared.InstallationOwner is { Length: > 0 }))
            return Refuse(GitSyncRefusal.Of(
                "gitsync.source.instanceAndOther",
                ("source", declared.SourceName), ("url", declared.RepositoryUrl)));

        if (declared.InstanceApp)
            return appTokens is { HasInstanceApp: true }
                ? appTokens.GetInstanceAppInstallationToken()
                : Refuse(GitSyncRefusal.Of(
                    "gitsync.source.instanceAppNotConnected",
                    ("source", declared.SourceName), ("url", declared.RepositoryUrl)));

        if (declared.TokenKey is { Length: > 0 } && declared.InstallationOwner is { Length: > 0 })
            return Refuse(GitSyncRefusal.Of(
                "gitsync.source.twoCredentials",
                ("source", declared.SourceName), ("url", declared.RepositoryUrl),
                ("key", ConfigurationKey(declared.TokenKey)), ("owner", declared.InstallationOwner)));

        if (declared.TokenKey is { Length: > 0 } key)
        {
            var configKey = ConfigurationKey(key);
            var token = configuration?[configKey]?.Trim();
            return string.IsNullOrEmpty(token)
                ? Refuse(GitSyncRefusal.Of(
                    "gitsync.source.tokenKeyEmpty",
                    ("source", declared.SourceName), ("url", declared.RepositoryUrl),
                    ("key", configKey), ("envKey", configKey.Replace(":", "__"))))
                : Observable.Return(token);
        }

        var owner = declared.InstallationOwner!;
        return appTokens is { IsConfigured: true }
            ? appTokens.GetInstallationToken(owner)
            : Refuse(GitSyncRefusal.Of(
                "gitsync.source.appNotConfigured",
                ("source", declared.SourceName), ("url", declared.RepositoryUrl), ("owner", owner)));
    });

    private IObservable<string> Refuse(GitSyncRefusal refusal)
    {
        var exception = new GitSourceCredentialException(refusal);
        logger?.LogError("{Refusal}", exception.Message);
        return Observable.Throw<string>(exception);
    }

    /// <summary>
    /// A declared key in configuration form: the environment spelling (<c>A__B__C</c>) and the
    /// configuration spelling (<c>A:B:C</c>) name the same key, and a record may be written with
    /// either. Pure.
    /// </summary>
    public static string ConfigurationKey(string declaredKey) =>
        (declaredKey ?? "").Trim().Replace("__", ":");

    /// <summary>
    /// A repository URL as the comparison reads it: trimmed, without a trailing slash or a
    /// <c>.git</c> suffix. Case is folded by the comparison (GitHub owner and repository names are
    /// case-insensitive). Null for blank. Pure.
    /// </summary>
    public static string? NormalizeRepository(string? repositoryUrl)
    {
        var url = (repositoryUrl ?? "").Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        return url.Length == 0 ? null : url;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
