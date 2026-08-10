using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Registers a freshly-installed package's registry sources — its agents and its skills — on EVERY
/// user of this instance, so the content a package ships is usable the moment it lands.
///
/// <para><b>The bug this closes.</b> A package installs its agents into its own partition
/// (<c>Essentials/Agent</c>), but the agent picker resolves from each user's
/// <c>AiSettings.AgentQueries</c>, which is written ONCE and never revisited. A user whose settings
/// predate the install therefore asks for <c>namespace:{user}/Agent|{space}/Agent|Agent</c> forever —
/// none of which match — and gets an empty picker with no error in any log. Skills had the identical
/// latent hole: a fire-and-forget <c>AddSkillSource</c> existed but nothing ever called it
/// (deleted with #683 — this hook's sequenced chain is the only registration path).</para>
///
/// <para><b>Every user, not just the installer.</b> A package is installed once, by an admin, for the
/// whole instance — so the sources belong on every account, including accounts created before the
/// install (they inherit the code defaults, which this then extends) and accounts that never open the
/// settings page.</para>
///
/// <para><b>Idempotent by construction.</b> It runs on install, on every package update, and on the
/// startup REPAIR pass — so "already correct" is the common case. A user whose rows are already
/// present is left completely untouched: no write, no version bump, no change feed.</para>
/// </summary>
/// <param name="hub">Hub supplying the workspace and mesh service.</param>
public sealed class AiSourcesInstallHook(IMessageHub hub) : IPartitionInstallHook
{
    /// <inheritdoc />
    public IObservable<Unit> OnPartitionInstalled(string partition)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return Observable.Return(Unit.Default);

        var services = hub.ServiceProvider;
        var meshService = services.GetService<IMeshService>();
        var accessService = services.GetService<AccessService>();
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<AiSourcesInstallHook>();
        if (meshService is null)
            return Observable.Return(Unit.Default);

        // Reconciling every account is a PLATFORM action — the install itself is gated to global
        // admins, and that gate is the authorization. Scoped around the writes, never ambient across
        // the pipeline's scheduler hops.
        return Users()
            .SelectMany(users => users.Count == 0
                ? Observable.Return(Unit.Default)
                : users
                    .Select(user => Reconcile(meshService, accessService, user, partition, logger))
                    .ToObservable()
                    .Merge(4)
                    .ToList()
                    .Select(_ => Unit.Default))
            .Catch((Exception ex) =>
            {
                // The package content is already written by the time hooks run; a failure here must
                // not fail the install. Surface it and move on.
                logger?.LogWarning(ex,
                    "Registering AI sources for partition {Partition} failed", partition);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>Every user on this instance. One snapshot — this runs at install, not per render.</summary>
    private IObservable<IReadOnlyList<string>> Users() =>
        hub.GetWorkspace()
            // No namespace filter: on a partitioned Postgres backend a `namespace:User` query
            // matches nothing (the users live in their own partitions).
            .GetQuery("ai-sources-users", $"nodeType:{UserNodeType} select:path,id,name,nodeType")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .Select(nodes => (IReadOnlyList<string>)nodes
                .Select(n => n.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                // 🚨 Drop the node-type DEFINITION. UserNodeType.CreateMeshNode registers itself
                // self-referentially — id "User" AND nodeType "User" — so `nodeType:User` returns
                // the definition alongside the real users, and its Id reads as a user called
                // "User". Everything downstream then targets that name: AiSettingsNodeType.PathFor
                // yields "User/_Memex/AiSettings", whose partition segment is the literal "User",
                // so the write lands on schema "user" — never provisioned, because no such user
                // was ever onboarded. Postgres answers 42P01 and the hook fails for EVERY
                // partition it registers ("Registering Collaboration sources for user User
                // failed"). A real user is always keyed by their principal id (rbuergi), never by
                // the type's own name.
                .Where(id => !string.Equals(id, UserNodeType, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());

    /// <summary>
    /// Adds the partition's agent + skill sources to one user's settings, writing ONLY when that
    /// actually changes something.
    /// </summary>
    private IObservable<Unit> Reconcile(
        IMeshService meshService,
        AccessService? accessService,
        string user,
        string partition,
        ILogger? logger)
    {
        var path = AiSettingsNodeType.PathFor(user);
        return hub.GetWorkspace()
            .GetQuery($"{AiSettingsNodeType.NodeType}|{user}",
                $"path:{path} nodeType:{AiSettingsNodeType.NodeType} select:path,id,name,nodeType,content")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(nodes =>
            {
                var node = nodes.FirstOrDefault(n => string.Equals(
                    n.NodeType, AiSettingsNodeType.NodeType, StringComparison.OrdinalIgnoreCase));
                var current = AiSettingsNodeType.Effective(
                    node, AiSettingsNodeType.BuildDefaults(hub.ServiceProvider), hub.JsonSerializerOptions);
                var merged = AiSettingsNodeType.MergePackageSources(current, partition);

                // Already correct — the common case. Do not write: a no-op write bumps the version
                // and fans a change through every subscriber for nothing.
                if (merged.AgentQueries.SequenceEqual(current.AgentQueries)
                    && merged.SkillQueries.SequenceEqual(current.SkillQueries))
                    return Observable.Return(Unit.Default);

                var target = node ?? MeshNode.FromPath(path) with
                {
                    NodeType = AiSettingsNodeType.NodeType,
                    Name = "AI Settings",
                };
                // Scoped around the WRITE, never ambient across the pipeline's scheduler hops —
                // an ambient impersonation does not survive those (PackageInstaller says the same).
                return Observable
                    .Using(
                        () => accessService?.ImpersonateAsSystem()
                              ?? (IDisposable)System.Reactive.Disposables.Disposable.Empty,
                        _ => meshService.CreateOrUpdateNode(target with { Content = merged }))
                    .Do(_ => logger?.LogInformation(
                        "Registered {Partition} agent + skill sources for {User}", partition, user))
                    .Select(_ => Unit.Default);
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "Registering {Partition} sources for user {User} failed", partition, user);
                return Observable.Return(Unit.Default);
            });
    }

    /// <summary>The User node type discriminator — matches <c>AddGraph()</c>'s standard types.</summary>
    private const string UserNodeType = "User";
}
