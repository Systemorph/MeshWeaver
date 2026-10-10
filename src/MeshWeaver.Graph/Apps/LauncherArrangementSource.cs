using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Apps;

/// <summary>
/// Reads a viewer's launcher ARRANGEMENT (<see cref="LauncherArrangement"/> at
/// <c>{user}/_Settings/Launcher</c>) live, and seeds it ONCE from the viewer's legacy
/// <c>{user}/_App</c> records the first time the directory renders for them.
/// <para>🚨 <b>The seed never feeds on its own writes.</b> Its trigger is "the arrangement node is
/// ABSENT", and its one write makes it present. Per viewer, at most one seed is in flight (a second
/// render joins it), and a seed that SUCCEEDED is never attempted again in this process — so a
/// stale negative from the index cannot repeat it — while a seed that FAILED is forgotten, so the
/// next render retries. The create is create-if-absent ("already exists" is the benign outcome of a
/// race), so it cannot overwrite an arrangement the viewer already made.</para>
/// <para>Existence is read with a <c>namespace:</c> LISTING, never a point read of the possibly
/// absent node (a point read of an absent path opens the storm-breaker on it — AGENTS.md CQRS).</para>
/// </summary>
public sealed class LauncherArrangementSource
{
    private readonly IMessageHub hub;
    private readonly ILogger<LauncherArrangementSource>? logger;
    private readonly object gate = new();
    // The OWNER of every seed connection (RootedRxConnectionRatchetGuard), released with the mesh hub.
    private readonly CompositeDisposable owned = new();
    private readonly ReleaseLane lane;
    private ImmutableHashSet<string> seeded = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<string, IObservable<Unit>> inFlight =
        ImmutableDictionary.Create<string, IObservable<Unit>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the source over the mesh hub.</summary>
    public LauncherArrangementSource(IMessageHub hub)
    {
        this.hub = hub;
        logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<LauncherArrangementSource>();
        lane = hub.ServiceProvider.GetRequiredService<ReleaseLane>();
        hub.RegisterForDisposal(owned);
    }

    /// <summary>The query listing <paramref name="owner"/>'s arrangement node (its settings namespace,
    /// narrowed to the type).</summary>
    public static string ArrangementQuery(string owner) =>
        $"namespace:{LauncherArrangementPaths.SettingsNamespaceFor(owner)} nodeType:{LauncherArrangementPaths.NodeType}";

    /// <summary>The query listing <paramref name="owner"/>'s legacy app records.</summary>
    public static string LegacyRecordsQuery(string owner) =>
        $"path:{owner}/{AppNodeType.UserNamespace} scope:children nodeType:{AppNodeType.NodeType}";

    /// <summary>
    /// The viewer's arrangement — live; <c>null</c> while the node does not exist. EXISTENCE comes
    /// from the settings listing (read as System — the viewer's own data, served back only to the
    /// viewer); CONTENT comes from the authoritative node stream once the listing has seen the
    /// node, so a rearrangement is painted from the write, never from a stale index row.
    /// </summary>
    public IObservable<LauncherArrangement?> Observe(string owner)
    {
        var core = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var options = hub.JsonSerializerOptions;
        var arrangementPath = LauncherArrangementPaths.PathFor(owner);
        return Fold(core.Query<MeshNode>(SystemRequest(ArrangementQuery(owner)), options))
            .Select(nodes => nodes.ContainsKey(arrangementPath))
            .DistinctUntilChanged()
            .Select(exists => exists
                ? cache.GetStream(arrangementPath, options)
                    .Select(node => (LauncherArrangement?)(node.ContentAs<LauncherArrangement>(options) ?? new LauncherArrangement()))
                : Observable.Return<LauncherArrangement?>(null))
            .Switch();
    }

    /// <summary>
    /// The viewer's arrangement as the launcher should paint with it: the live arrangement once it
    /// exists. While it does NOT exist, nothing is emitted — the seed runs (or is joined) and the
    /// live read then answers with the created node, so rows are never published before there is an
    /// arrangement a drop could write to. A FAILED seed is logged and answered with <c>null</c>
    /// (the launcher still paints, in its default arrangement), and the next render retries it.
    /// </summary>
    public IObservable<LauncherArrangement?> ObserveSeeded(string owner) =>
        Observe(owner)
            .Select(current => current is not null
                ? Observable.Return<LauncherArrangement?>(current)
                : EnsureSeeded(owner)
                    .IgnoreElements()
                    .Select(_ => (LauncherArrangement?)null)
                    .Catch((Exception exception) =>
                    {
                        logger?.LogError(exception,
                            "[AppDirectory] the launcher arrangement of {Owner} could not be seeded; painting the default arrangement", owner);
                        return Observable.Return<LauncherArrangement?>(null);
                    }))
            .Switch();

    /// <summary>Whether this process has seeded <paramref name="owner"/>'s arrangement.</summary>
    public bool IsSeeded(string owner)
    {
        lock (gate)
            return seeded.Contains(owner);
    }

    /// <summary>
    /// The seed of <paramref name="owner"/>'s arrangement — at most one in flight per viewer (a
    /// concurrent caller joins it), never again once it succeeded in this process (the result is
    /// then <see cref="Observable.Never{T}()"/>: the live read is what answers), retried after a
    /// failure. Completes when the create landed or lost a benign race.
    /// </summary>
    public IObservable<Unit> EnsureSeeded(string owner)
    {
        lock (gate)
        {
            if (seeded.Contains(owner))
                return Observable.Never<Unit>();
            if (inFlight.TryGetValue(owner, out var running))
                return running;
            var seed = Seed(owner)
                .Do(
                    _ => { },
                    _ => { lock (gate) inFlight = inFlight.Remove(owner); },
                    () =>
                    {
                        lock (gate)
                        {
                            inFlight = inFlight.Remove(owner);
                            seeded = seeded.Add(owner);
                        }
                    })
                .Replay(1)
                .AutoConnectOwnedBy(owned, lane, nameof(LauncherArrangementSource));
            inFlight = inFlight.SetItem(owner, seed);
            return seed;
        }
    }

    private IObservable<Unit> Seed(string owner)
    {
        var core = hub.ServiceProvider.GetRequiredService<IMeshQueryCore>();
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return core.Query<MeshNode>(SystemRequest(LegacyRecordsQuery(owner)), options)
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => FromLegacyRecords(change.Items, options))
            .SelectMany(arrangement =>
            {
                var node = BuildNode(owner, arrangement);
                // The viewer's own data, created as the viewer — never as the hub.
                return access.RunAs(new AccessContext { ObjectId = owner, Name = owner },
                        () => mesh.CreateNode(node))
                    .Select(_ => Unit.Default)
                    .Catch((Exception exception) =>
                    {
                        // The TYPED classifier — never a message match, which would read an
                        // unrelated failure as a won race and mark the viewer seeded for good.
                        if (exception.IsNodeAlreadyExists())
                            return Observable.Return(Unit.Default);
                        logger?.LogWarning(exception,
                            "[AppDirectory] seeding the launcher arrangement of {Owner} failed", owner);
                        return Observable.Throw<Unit>(exception);
                    });
            })
            .Do(_ => logger?.LogInformation(
                "[AppDirectory] seeded the launcher arrangement of {Owner} from its _App records", owner));
    }

    /// <summary>The arrangement node for <paramref name="owner"/>. Pure.</summary>
    public static MeshNode BuildNode(string owner, LauncherArrangement arrangement) =>
        new(LauncherArrangementPaths.NodeId, LauncherArrangementPaths.SettingsNamespaceFor(owner))
        {
            NodeType = LauncherArrangementPaths.NodeType,
            Name = "Launcher",
            State = MeshNodeState.Active,
            Content = arrangement,
        };

    /// <summary>
    /// The arrangement the legacy records carried: per record, its group, order and
    /// <c>customGroup</c> flag, keyed by the app it stands for — the package root
    /// (<see cref="App.Plugin"/>), or for an area app (<c>~/Inbox</c>) the record's id. Records that
    /// carry no placement are left out (the directory's defaults apply). Pure.
    /// </summary>
    public static LauncherArrangement FromLegacyRecords(IEnumerable<MeshNode> records, JsonSerializerOptions options)
    {
        var entries = ImmutableDictionary.CreateBuilder<string, LauncherEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record.ContentAs<App>(options) is not { } app)
                continue;
            if (app.Group is null && app.Order == 0)
                continue;
            var appId = string.IsNullOrWhiteSpace(app.Plugin) ? record.Id : app.Plugin.Trim('/');
            entries[appId] = new LauncherEntry
            {
                Group = app.Group,
                Order = app.Order,
                CustomGroup = app.CustomGroup,
            };
        }
        return new LauncherArrangement { Entries = entries.ToImmutable() };
    }

    private static MeshQueryRequest SystemRequest(string query) =>
        MeshQueryRequest.FromQuery(query) with { UserId = WellKnownUsers.System };

    /// <summary>Folds a query's change stream into the live set, keyed by path.</summary>
    internal static IObservable<ImmutableDictionary<string, MeshNode>> Fold(IObservable<QueryResultChange<MeshNode>> changes) =>
        changes
            .Scan(ImmutableDictionary.Create<string, MeshNode>(StringComparer.OrdinalIgnoreCase), (acc, change) =>
            {
                var next = change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset ? acc.Clear() : acc;
                foreach (var node in change.Items)
                {
                    if (string.IsNullOrEmpty(node.Path))
                        continue;
                    next = change.ChangeType == QueryChangeType.Removed
                        ? next.Remove(node.Path)
                        : next.SetItem(node.Path, node);
                }
                return next;
            });
}
