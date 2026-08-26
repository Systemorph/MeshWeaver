using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Seeds the platform default app records (<c>Admin/HomeConfig.DefaultApps</c> — Store, Doc, the
/// Threads app) for a user, exactly once.
///
/// <para><b>Why this moved out of the render path.</b> The home used to seed defaults from
/// <c>CatalogAreaView</c> whenever the viewer's grid came back EMPTY. Emptiness was standing in for
/// "this user has not been set up yet", and that proxy was only ever valid while nothing else could
/// write an app record. The Store now writes one at install time
/// (MeshWeaver.Plugins#618) — so a user who acquires a package BEFORE first opening their home has
/// a non-empty grid on their first render, the seeding never fires, and they permanently lose the
/// defaults. Including the Store tile itself, which is the one the seeding exists to guarantee:
/// without it a fresh user has no way to reach the Store at all.</para>
///
/// <para>A run-once logon action says what the proxy was reaching for: this runs once per user
/// because the ledger says it has run, not because their grid happened to look a particular way.
/// A user who later deletes every tile keeps them deleted — which is right, and something the old
/// trigger got wrong in the other direction (an emptied grid re-seeded itself on the next render).
/// </para>
///
/// <para>🚨 <b>Create-if-absent, never overwrite.</b> Each record is created independently and an
/// "already exists" is a benign no-op, so this cannot clobber a record the Store wrote for the same
/// app, and two logons racing cannot produce a duplicate. The action is therefore safe to run even
/// where the ledger write is lost — which matters, because the ledger is atomic with the PROFILE,
/// not with these node writes (see <see cref="LogonActionOutcome"/>): this is at-least-once, so it
/// has to be idempotent on its own, and it is.</para>
/// </summary>
public sealed class SeedDefaultAppsLogonAction : ILogonAction
{
    /// <summary>How long to wait for a real <c>Admin/HomeConfig</c> to answer before settling for
    /// what we have. Short, because on a portal with no such node this is pure latency on every
    /// first logon — and the action runs off the critical path.</summary>
    private static readonly TimeSpan ConfigSettleBound = TimeSpan.FromSeconds(2);

    /// <summary>Outer bound on the config read. A slow or absent config costs the seed, never the
    /// logon — and an unrecorded action simply retries on the next one.</summary>
    private static readonly TimeSpan ConfigReadBound = TimeSpan.FromSeconds(10);

    /// <summary>🚨 The ledger key. Changing it re-runs the action for every existing user — which,
    /// for this action, means re-creating records they may have deliberately deleted.</summary>
    public string Id => "seed-default-apps";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.RunOnce;

    /// <summary>Before the pin migration and the icon adoption: those operate ON records, so the
    /// records should exist first. Ordering is not required for correctness — each is independently
    /// idempotent — it just avoids a first logon that pins or heals nothing and then seeds.</summary>
    public int Order => -100;

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.SeedDefaultApps");
        var ownerId = context.UserPath;
        if (mesh is null || string.IsNullOrEmpty(ownerId))
            return Observable.Return(LogonActionOutcome.Nothing);

        // The same admin-editable Admin/HomeConfig the home reads, so the defaults a user is seeded
        // with are the defaults the deployment declares — instance-specific, not compiled in.
        //
        // 🚨 Getting the number of emissions right matters more than it looks. Observe() is
        // StartWith(Defaults) + DistinctUntilChanged, so a portal with NO materialized HomeConfig
        // node — or one whose config equals the shipped defaults — emits exactly ONCE. Skipping
        // that first emission to "avoid acting on the placeholder" therefore waits forever on
        // precisely the fresh deployment this action exists to serve, times out, and seeds nothing:
        // the original bug, reintroduced from the other side.
        //
        // So: take up to two emissions (the placeholder, then the real config if a node answers),
        // stop waiting at the settle bound either way, and use whichever arrived last. One
        // emission ⇒ the declared/shipped defaults after the bound; two ⇒ the configured set as
        // soon as the query answers. Always terminates, and never seeds the shipped set at a
        // portal that has configured its own.
        return HomeConfigNodeType
            .Observe(context.Hub.GetWorkspace(), context.Hub.JsonSerializerOptions)
            .Take(2)
            .TakeUntil(Observable.Timer(ConfigSettleBound))
            .LastAsync()
            .Timeout(ConfigReadBound)
            .SelectMany(config =>
            {
                var specs = UserActivityLayoutAreas.AppRecordSpecs(config, ownerId).ToArray();
                if (specs.Length == 0)
                    return Observable.Return(LogonActionOutcome.Nothing);

                return specs
                    .Select(spec => Create(mesh, ownerId, spec, logger))
                    .Concat()
                    .ToArray()
                    .Select(_ => LogonActionOutcome.Nothing);
            })
            // Anything that got this far unhandled — the config read, or a create that was not an
            // already-exists — is logged HERE and rethrown, so the runner does not record the
            // action as done. An unrecorded run-once action retries on the next logon, which is
            // exactly right for one whose every step is idempotent.
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception, "Seeding default apps for {Owner} failed", ownerId);
                return Observable.Throw<LogonActionOutcome>(exception);
            });
    }

    /// <summary>
    /// Creates one record, treating "already exists" — and ONLY that — as success. That is what
    /// makes this safe against the Store having written the same app's record first, and against a
    /// concurrent logon: the create is the claim, and losing the race is a no-op rather than an
    /// overwrite.
    ///
    /// <para>🚨 Every OTHER failure propagates, and must. This is a RunOnce action, so the runner
    /// commits the ledger entry when it completes — swallowing a transient fault (access denied, a
    /// missing parent, a storage blip) would mark the action permanently done for that user while
    /// leaving them short a tile, with no mechanism that ever revisits it. Failing instead leaves
    /// the ledger unwritten so the next logon retries, which costs nothing because the creates are
    /// idempotent.</para>
    /// </summary>
    private static IObservable<Unit> Create(
        IMeshService mesh, string ownerId, UserActivityLayoutAreas.AppRecordSpec spec, ILogger? logger)
    {
        var node = UserActivityLayoutAreas.BuildAppRecord(ownerId, spec);
        return mesh.CreateNode(node)
            .Select(_ => Unit.Default)
            .Catch((Exception exception) =>
            {
                if (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("Default app record {Path} already existed", node.Path);
                    return Observable.Return(Unit.Default);
                }

                logger?.LogWarning(exception, "Default app record create failed at {Path}", node.Path);
                return Observable.Throw<Unit>(exception);
            });
    }
}
