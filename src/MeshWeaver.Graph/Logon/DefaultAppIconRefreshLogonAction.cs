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
/// Converges a DEFAULT app record's icon to the artwork today's seed would give it, for users who
/// were seeded by an older one.
///
/// <para><b>Why this is needed at all.</b> Changing a default's artwork only reaches people who
/// have not been seeded yet. Every existing user keeps whatever their record was stamped with,
/// forever, because nothing revisits a record once it exists — so a redraw ships to new accounts and
/// silently skips everybody else. That is a half-finished change, and this is the other half.</para>
///
/// <para>🚨 <b>It can only move a record OFF a value core itself shipped and retired</b>
/// (<see cref="AppIconAdoption.SupersededDefaultIcons"/>). Not "anything that differs from the
/// current seed" — that would overwrite an icon a VIEWER chose, which is theirs, and it would fight
/// the Store, which converges the icons of the records IT owns (MeshWeaver.Plugins#624). Two
/// writers on one field with overlapping conditions is how a tile starts flickering between two
/// answers on alternate logons. The historical list keeps this to exactly the records core is
/// responsible for.</para>
///
/// <para><b>EveryLogon, not run-once</b>, for the same reason the icon adoption is: a run-once
/// refresh would converge whatever was stale on the day it ran and leave every later redraw
/// unreachable, with the ledger recording it as done. The steady-state cost is one query whose
/// result matches nothing.</para>
/// </summary>
public sealed class DefaultAppIconRefreshLogonAction : ILogonAction
{
    /// <inheritdoc />
    public string Id => "refresh-default-app-icons";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>After seeding (records must exist) and beside the icon adoption, which handles the
    /// disjoint case of a record with no icon at all.</summary>
    public int Order => -50;

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.DefaultAppIconRefresh");
        var ownerId = context.UserPath;
        if (mesh is null || string.IsNullOrEmpty(ownerId))
            return Observable.Return(LogonActionOutcome.Nothing);

        return HomeConfigNodeType
            .Observe(context.Hub.GetWorkspace(), context.Hub.JsonSerializerOptions)
            .Take(2)
            .TakeUntil(Observable.Timer(TimeSpan.FromSeconds(2)))
            .LastAsync()
            .SelectMany(config => Converge(mesh, context, ownerId, config, logger))
            // A cosmetic repair must never cost a logon, and EveryLogon means a throw here would be
            // logged on every single sign-in. Nothing, quietly, and try again next time.
            .Catch<LogonActionOutcome, Exception>(exception =>
            {
                logger?.LogDebug(exception, "Default app icon refresh skipped for {Owner}", ownerId);
                return Observable.Return(LogonActionOutcome.Nothing);
            });
    }

    private static IObservable<LogonActionOutcome> Converge(
        IMeshService mesh, LogonActionContext context, string ownerId,
        HomeConfig config, ILogger? logger)
    {
        // What today's seed WOULD stamp, keyed by record id — the target of the convergence.
        var current = UserActivityLayoutAreas.AppRecordSpecs(config, ownerId)
            .ToDictionary(spec => spec.Id, spec => spec.Icon, StringComparer.OrdinalIgnoreCase);
        if (current.Count == 0)
            return Observable.Return(LogonActionOutcome.Nothing);

        return mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{ownerId}/{AppNodeType.UserNamespace} scope:children "
                + $"nodeType:{AppNodeType.NodeType} select:path,id,namespace,name,nodeType,icon"))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.ToArray())
            .Take(1)
            .SelectMany(records =>
            {
                var stale = records
                    .Select(record => (record, icon: current.GetValueOrDefault(record.Id)))
                    .Where(pair => AppIconAdoption.NeedsIconRefresh(pair.record, pair.icon))
                    .ToArray();
                if (stale.Length == 0)
                    return Observable.Return(LogonActionOutcome.Nothing);

                return stale
                    .Select(pair => Refresh(context, pair.record, pair.icon!, logger))
                    .Concat()
                    .ToArray()
                    .Select(_ => LogonActionOutcome.Nothing);
            });
    }

    private static IObservable<Unit> Refresh(
        LogonActionContext context, MeshNode record, string icon, ILogger? logger)
    {
        logger?.LogDebug("Refreshing superseded default icon on {Path}", record.Path);
        return context.Hub.GetWorkspace()
            .GetMeshNodeStream(record.Path)
            // Re-check inside the update: between the read and the write the Store or the viewer
            // may have chosen an icon, and this repair must never overwrite a real answer.
            .Update(cur => AppIconAdoption.NeedsIconRefresh(cur, icon) ? cur with { Icon = icon } : cur)
            .Select(_ => Unit.Default)
            .Catch((Exception exception) =>
            {
                logger?.LogDebug(exception, "Icon refresh failed for {Path}", record.Path);
                return Observable.Return(Unit.Default);
            });
    }
}
