using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// A package source a global administrator added ON THIS INSTANCE — stored on
/// <c>Admin/PackageSource-{id}</c>, never on the control instance and never in the deployment
/// record (policy <c>control-knows-basic-config-only</c>,
/// <c>Doc/Architecture/InstanceRepositoryAccess</c>). The instance's own counterpart of a configured
/// <c>PluginCatalog:Sources:N</c> entry.
/// </summary>
public record InstancePackageSourceContent
{
    /// <summary>The repository URL, e.g. <c>https://github.com/acme/reporting</c>.</summary>
    public string Repo { get; init; } = "";

    /// <summary>The branch to read; <c>main</c> when blank.</summary>
    public string? Ref { get; init; }

    /// <summary>The source's name — what <c>Source/Package</c> install patterns and logs call it;
    /// the repository's name when blank (<see cref="InstancePackageSources.NameOf"/>).</summary>
    public string? Name { get; init; }

    /// <summary>The package ids this instance installs by default from this source — the instance's
    /// own counterpart of the deployment's <c>PluginCatalog:InstallByDefault</c>.</summary>
    public ImmutableList<string> InstallByDefault { get; init; } = ImmutableList<string>.Empty;

    /// <summary>Who added the source (a user id; attribution only).</summary>
    public string? AddedBy { get; init; }

    /// <summary>When the source was added.</summary>
    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>Who last changed the source.</summary>
    public string? UpdatedBy { get; init; }

    /// <summary>When the source last changed.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// The instance's own package sources: the node type, the verbs a global administrator uses, and
/// the pure helpers every reader shares. See <c>Doc/Architecture/InstanceRepositoryAccess</c>.
///
/// <para>Contract: every verb checks rights FIRST, as the caller (a global administrator), and then
/// writes as system; a refusal writes nothing and is a localizable <see cref="GitSyncRefusal"/>.
/// Readers never write: they read the live <see cref="InstancePackageSourceCatalog"/>.</para>
/// </summary>
public static class InstancePackageSources
{
    /// <summary>The node type of a source.</summary>
    public const string NodeType = "InstancePackageSource";

    /// <summary>The id prefix of a source's node.</summary>
    public const string IdPrefix = "PackageSource-";

    /// <summary>The branch read when a source names none.</summary>
    public const string DefaultRef = "main";

    private static readonly Regex OwnerRepo = new(
        @"^https://github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PackageId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

    private static readonly Regex RefShape = new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$", RegexOptions.Compiled);

    /// <summary>The node type definition.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Instance Package Source",
        Icon = "/static/NodeTypeIcons/code.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<InstancePackageSourceContent>()),
    };

    /// <summary>
    /// A repository URL in the one form a source is stored and compared in —
    /// <c>https://github.com/{owner}/{repo}</c>, trimmed, without a trailing slash or <c>.git</c> —
    /// or null when it is not a GitHub repository URL. Only github.com: the identity this page
    /// adds is a GitHub App. Pure.
    /// </summary>
    public static string? NormalizeRepo(string? repo)
    {
        var url = GitSourceCredentials.NormalizeRepository(repo);
        if (url is null)
            return null;
        var match = OwnerRepo.Match(url);
        return match.Success && match.Groups["repo"].Value is not ("." or "..")
            ? $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}"
            : null;
    }

    /// <summary>The node id for <paramref name="repo"/>: <c>PackageSource-{owner}--{repo}</c>, lower
    /// case, so two spellings of one repository address one node. Null for a URL
    /// <see cref="NormalizeRepo"/> refuses. Pure.</summary>
    public static string? IdOf(string? repo) =>
        NormalizeRepo(repo) is { } url
            ? IdPrefix + url["https://github.com/".Length..].ToLowerInvariant().Replace("/", "--")
            : null;

    /// <summary>The node path for <paramref name="repo"/>, or null. Pure.</summary>
    public static string? PathOf(string? repo) =>
        IdOf(repo) is { } id ? $"{InstanceAdminRecords.Partition}/{id}" : null;

    /// <summary>The source's name: the declared one, else the repository's name. Pure.</summary>
    public static string NameOf(InstancePackageSourceContent source) =>
        !string.IsNullOrWhiteSpace(source.Name)
            ? source.Name.Trim()
            : (NormalizeRepo(source.Repo) ?? source.Repo).TrimEnd('/').Split('/')[^1];

    /// <summary>The branch to read: the declared one, else <see cref="DefaultRef"/>. Pure.</summary>
    public static string RefOf(InstancePackageSourceContent source) =>
        string.IsNullOrWhiteSpace(source.Ref) ? DefaultRef : source.Ref.Trim();

    /// <summary>
    /// The <c>Source/Package</c> install entries the instance's sources name — each source's
    /// <see cref="InstancePackageSourceContent.InstallByDefault"/> under its <see cref="NameOf"/>.
    /// Pure.
    /// </summary>
    public static IReadOnlyList<string> InstallEntries(IEnumerable<InstancePackageSourceContent> sources) =>
        [.. sources.SelectMany(s => s.InstallByDefault
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => $"{NameOf(s)}/{p.Trim()}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Adds (or re-points) the source for <paramref name="repo"/>. A global administrator only;
    /// writes as system. Emits the stored content. Cold.
    /// </summary>
    /// <param name="hub">The caller's hub.</param>
    /// <param name="repo">A GitHub repository URL.</param>
    /// <param name="gitRef">The branch; <see cref="DefaultRef"/> when blank.</param>
    /// <param name="name">The source's name; the repository's when blank.</param>
    public static IObservable<InstancePackageSourceContent> Add(
        IMessageHub hub, string repo, string? gitRef = null, string? name = null) =>
        Observable.Defer(() =>
        {
            if (NormalizeRepo(repo) is not { } url)
                return Observable.Throw<InstancePackageSourceContent>(
                    GitSyncRefusal.Of("instanceSources.error.notGitHubRepo", ("repo", repo ?? "")).ToException());
            var branch = string.IsNullOrWhiteSpace(gitRef) ? null : gitRef.Trim();
            if (branch is not null && !RefShape.IsMatch(branch))
                return Observable.Throw<InstancePackageSourceContent>(
                    GitSyncRefusal.Of("instanceSources.error.badRef", ("ref", branch)).ToException());
            var displayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (displayName is not null && !PackageId.IsMatch(displayName))
                return Observable.Throw<InstancePackageSourceContent>(
                    GitSyncRefusal.Of("instanceSources.error.badName", ("name", displayName)).ToException());
            return Write(hub, url, mustExist: false, (current, user, now) => (current ?? new InstancePackageSourceContent
            {
                AddedBy = user,
                AddedAt = now,
            }) with
            {
                Repo = url,
                Ref = branch,
                Name = displayName,
                UpdatedBy = user,
                UpdatedAt = now,
            });
        });

    /// <summary>
    /// Sets the package ids installed by default from the source for <paramref name="repo"/>. A
    /// global administrator only. Emits the stored content. Cold.
    /// </summary>
    public static IObservable<InstancePackageSourceContent> SetInstallByDefault(
        IMessageHub hub, string repo, IEnumerable<string> packageIds) =>
        Observable.Defer(() =>
        {
            var ids = (packageIds ?? []).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableList();
            if (ids.FirstOrDefault(p => !PackageId.IsMatch(p)) is { } bad)
                return Observable.Throw<InstancePackageSourceContent>(
                    GitSyncRefusal.Of("instanceSources.error.badPackage", ("package", bad)).ToException());
            return Write(hub, NormalizeRepo(repo) ?? repo ?? "", mustExist: true, (current, user, now) => current! with
            {
                InstallByDefault = ids,
                UpdatedBy = user,
                UpdatedAt = now,
            });
        });

    /// <summary>
    /// Removes the source for <paramref name="repo"/>. A global administrator only. Packages already
    /// installed from it stay installed: removing a source stops new installs and the feed, it does
    /// not uninstall. Cold; emits once.
    /// </summary>
    public static IObservable<System.Reactive.Unit> Remove(IMessageHub hub, string repo) =>
        Observable.Defer(() =>
        {
            if (PathOf(repo) is not { } path)
                return Observable.Throw<System.Reactive.Unit>(
                    GitSyncRefusal.Of("instanceSources.error.notGitHubRepo", ("repo", repo ?? "")).ToException());
            var catalog = hub.ServiceProvider.GetRequiredService<InstancePackageSourceCatalog>();
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            return InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, _ =>
                catalog.WhenLoaded.SelectMany(_ => catalog.Entry(path) is null
                    ? Observable.Throw<System.Reactive.Unit>(
                        GitSyncRefusal.Of("instanceSources.error.notFound", ("repo", repo)).ToException())
                    : mesh.DeleteNode(path).Take(1).Select(_ => System.Reactive.Unit.Default)));
        });

    private static GitSyncRefusal NotAdmin() => GitSyncRefusal.Of("instanceSources.error.notAdmin");

    private static IObservable<InstancePackageSourceContent> Write(
        IMessageHub hub, string url, bool mustExist,
        Func<InstancePackageSourceContent?, string, DateTimeOffset, InstancePackageSourceContent> fold) =>
        Observable.Defer(() =>
        {
            if (PathOf(url) is not { } path || IdOf(url) is not { } id)
                return Observable.Throw<InstancePackageSourceContent>(
                    GitSyncRefusal.Of("instanceSources.error.notGitHubRepo", ("repo", url)).ToException());
            var catalog = hub.ServiceProvider.GetRequiredService<InstancePackageSourceCatalog>();
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var options = hub.JsonSerializerOptions;

            IObservable<InstancePackageSourceContent> Update(string user) =>
                hub.GetWorkspace().GetMeshNodeStream(path)
                    .Update<InstancePackageSourceContent>((node, current) =>
                        node with { Content = fold(current, user, DateTimeOffset.UtcNow) })
                    .Take(1)
                    .Select(n => n.ContentAs<InstancePackageSourceContent>(options) ?? new InstancePackageSourceContent());

            IObservable<InstancePackageSourceContent> Create(string user)
            {
                var content = fold(null, user, DateTimeOffset.UtcNow);
                var node = new MeshNode(id, InstanceAdminRecords.Partition)
                {
                    NodeType = NodeType,
                    Name = NameOf(content),
                    State = MeshNodeState.Active,
                    Content = content,
                };
                return mesh.CreateNode(node).Take(1).Select(_ => content)
                    .Catch<InstancePackageSourceContent, Exception>(ex => InstanceAdminRecords.IsAlreadyExists(ex)
                        ? Update(user)
                        : Observable.Throw<InstancePackageSourceContent>(ex));
            }

            return InstanceAdminRecords.AsGlobalAdmin(hub, NotAdmin, user =>
                catalog.WhenLoaded.SelectMany(_ => catalog.Entry(path) is null
                    ? mustExist
                        ? Observable.Throw<InstancePackageSourceContent>(
                            GitSyncRefusal.Of("instanceSources.error.notFound", ("repo", url)).ToException())
                        : Create(user)
                    : Update(user)));
        });
}

/// <summary>
/// The live set of this instance's own package sources (<c>Admin/PackageSource-*</c>). One query
/// per mesh, as system; read by the Store feed, the default install and the credential resolution.
/// </summary>
public sealed class InstancePackageSourceCatalog(IMessageHub hub, ILogger<InstancePackageSourceCatalog>? logger = null)
    : InstanceAdminRecordCatalog<InstancePackageSourceContent>(hub, InstancePackageSources.NodeType, logger)
{
    /// <summary>Every source, in repository order.</summary>
    public IReadOnlyList<InstancePackageSourceContent> Sources =>
        [.. Entries.Values.OrderBy(s => s.Repo, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The source for <paramref name="repositoryUrl"/>, however the URL is spelled, or null.</summary>
    public InstancePackageSourceContent? For(string? repositoryUrl) =>
        InstancePackageSources.PathOf(repositoryUrl) is { } path ? Entry(path) : null;

    /// <inheritdoc />
    protected override bool Accepts(string path, InstancePackageSourceContent content) =>
        string.Equals(InstancePackageSources.PathOf(content.Repo), path, StringComparison.OrdinalIgnoreCase);
}
