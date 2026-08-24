using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Gives every one of the user's installed-app records the icon of the app it points at, so the
/// home's Apps grid is a grid of recognisable icons rather than identical placeholders. The decision
/// per record is <see cref="AppIconAdoption"/>; this is the plumbing around it.
///
/// <para>🚨 <b>EveryLogon, not RunOnce</b> — and the argument is that new work keeps arriving. A
/// run-once action would repair whatever the user had the day it first ran and never look again, so
/// every app installed afterwards would keep its placeholder forever, and the ledger would record
/// the feature as "done". The cost of running every time is bounded by a check, not by a ledger: one
/// query over the user's own <c>_App</c> children, and — in the steady state, once the Store stamps
/// real icons — zero target lookups and zero writes, because nothing passes
/// <see cref="AppIconAdoption.NeedsIcon"/>. That is the shape every every-logon action must have:
/// cheap enough to be wrong about, rather than remembered.</para>
///
/// <para>Runs as the LOGGING-ON USER. These are the user's own records in the user's own partition;
/// the platform writing them as <c>system-security</c> would be the platform editing someone's home
/// behind their back, and it is not necessary — at logon a real identity exists.</para>
/// </summary>
public sealed class AppIconAdoptionLogonAction : ILogonAction
{
    /// <summary>The ledger key this action would use if it were run-once. It is not, deliberately —
    /// see the type remarks — but the id still has to be stable and unique.</summary>
    public string Id => "platform.app-icon-adoption";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>Runs late: a migration that changes what is installed should do so before the icons
    /// of what is installed are repaired.</summary>
    public int Order => 100;

    /// <summary>Bound on each mesh query. A slow index costs the icons, never the logon.</summary>
    private static readonly TimeSpan QueryBound = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.AppIconAdoption");
        if (mesh is null)
            return Observable.Return(LogonActionOutcome.Nothing);

        return ReadRecords(mesh, context.UserPath)
            .SelectMany(records =>
            {
                // The cheap check that makes EveryLogon affordable: in the steady state nothing
                // needs an icon, and the action ends here having issued exactly one query.
                var needy = records
                    .Where(AppIconAdoption.NeedsIcon)
                    .Select(record => (record, target: AppIconAdoption.TargetOf(
                        record, record.ContentAs<App>(context.Hub.JsonSerializerOptions)?.Plugin)))
                    .Where(pair => pair.target is not null)
                    .ToArray();
                if (needy.Length == 0)
                    return Observable.Return(LogonActionOutcome.Nothing);

                return needy
                    .Select(pair => Adopt(mesh, context, pair.record, pair.target!, logger))
                    .Concat()
                    .ToArray()
                    .Select(_ => LogonActionOutcome.Nothing);
            })
            // The record set is the user's own partition; failing to read it is a cosmetic miss.
            // Returning Nothing (rather than throwing) keeps this an every-logon no-op instead of a
            // logged failure on every single logon.
            .Catch<LogonActionOutcome, Exception>(ex =>
            {
                logger?.LogDebug(ex, "App icon adoption skipped for {User}", context.UserPath);
                return Observable.Return(LogonActionOutcome.Nothing);
            });
    }

    /// <summary>
    /// The user's installed-app records — the same query the home's Apps grid runs, so this costs
    /// what painting the grid already costs.
    ///
    /// <para>🚨 No <c>select:</c> projection. The decision needs <see cref="MeshNode.MainNode"/>
    /// (that is the app the record points at), and a projection that omits it makes
    /// <see cref="AppIconAdoption.TargetOf"/> return null for every record — a silently inert action
    /// that queries, decides "nothing to adopt", and writes nothing, forever.</para>
    /// </summary>
    private static IObservable<IReadOnlyCollection<MeshNode>> ReadRecords(IMeshService mesh, string userPath) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{userPath}/{AppNodeType.UserNamespace} scope:children "
                + $"nodeType:{AppNodeType.NodeType}"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => (IReadOnlyCollection<MeshNode>)change.Items.ToArray())
            .Take(1)
            .Timeout(QueryBound);

    /// <summary>Resolve one record's target, and write the target's icon onto the record when the
    /// target has a better one. Never faults the run: one unreachable target is a cosmetic miss.</summary>
    private static IObservable<System.Reactive.Unit> Adopt(
        IMeshService mesh, LogonActionContext context, MeshNode record, string target, ILogger? logger)
    {
        return mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{target} select:path,id,namespace,name,nodeType,icon"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.FirstOrDefault()?.Icon)
            .Take(1)
            .Timeout(QueryBound)
            .SelectMany(targetIcon =>
            {
                if (AppIconAdoption.IconToAdopt(record, targetIcon) is not { } icon)
                    return Observable.Return(System.Reactive.Unit.Default);
                logger?.LogDebug("Adopting icon of {Target} onto app record {Path}", target, record.Path);
                // Re-check INSIDE the update: the Store may have stamped a real icon between the
                // read and the write, and this repair must never overwrite a better answer than its
                // own. The write carries the logging-on user's identity — these are their records.
                return context.Hub.GetWorkspace()
                    .GetMeshNodeStream(record.Path)
                    .Update(current => AppIconAdoption.NeedsIcon(current) ? current with { Icon = icon } : current)
                    .Select(_ => System.Reactive.Unit.Default);
            })
            .Catch<System.Reactive.Unit, Exception>(ex =>
            {
                logger?.LogDebug(ex, "Icon adoption skipped for app record {Path}", record.Path);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }
}
