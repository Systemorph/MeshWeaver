using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>What one sync source's update ended as — open string constants.</summary>
public static class ModuleSourceUpdateOutcome
{
    /// <summary>The import ran; its activity ended Succeeded or Warning (a seal hold or a declined module is a Warning, named).</summary>
    public const string Updated = "Updated";

    /// <summary>Not a module source here, or not importable (no repository, export-only) — the detail names which.</summary>
    public const string Skipped = "Skipped";

    /// <summary>The import failed, was cancelled, or did not finish within its budget — the detail names which.</summary>
    public const string Failed = "Failed";
}

/// <summary>One sync source's row in <see cref="ModuleSourceUpdate.UpdateAll"/>.</summary>
/// <param name="ConfigPath">The <c>{Space}/_GitSync[/{sourceId}]</c> node.</param>
/// <param name="Space">The space the source imports into.</param>
/// <param name="SourceId">The additional source's id, or null for the primary.</param>
/// <param name="Outcome">A <see cref="ModuleSourceUpdateOutcome"/> value.</param>
/// <param name="Detail">The row's own sentence: the import's outcome, its commit, and every module's per-manifest-hash judgement (declines named).</param>
public sealed record ModuleSourceUpdateRow(string ConfigPath, string Space, string? SourceId, string Outcome, string Detail);

/// <summary>
/// "Sync every GitSynced MODULE source to its branch HEAD, now" — the SAME import as
/// <c>git_hub_sync op=update</c> (<see cref="GitHubActivityExtensions.UpdateToLatestFromGitHub"/>), run once per
/// source, as System, one source at a time, each followed to the end of its activity. Used by the
/// instance reboot (<c>Doc/Architecture/InstanceReboot</c>, step 1). It forks nothing: the seal is
/// asked exactly as for a person's update (a held source lands nothing and says why), and every module
/// is judged alone by its manifest hash, with an incompatible floor or an unmet dependency DECLINED by
/// name (<see cref="ModuleSyncDecision"/>).
///
/// <para>A MODULE source is one whose config has recorded a module-bearing tree
/// (<see cref="GitHubSyncConfig.ModuleVersions"/> or <see cref="GitHubSyncConfig.ModuleOutcomes"/> is
/// set). A content-only space is not re-imported by a reboot: its own sync keeps it current, and an
/// unasked import of a person's space is not what "reboot" means.</para>
/// </summary>
public static class ModuleSourceUpdate
{
    /// <summary>How long one source's import may take before its row reads Failed (not finished).</summary>
    public static readonly TimeSpan ActivityBudget = TimeSpan.FromMinutes(10);

    /// <summary>How long the listing of sync sources may take.</summary>
    public static readonly TimeSpan ListingBudget = TimeSpan.FromSeconds(30);

    /// <summary>The config node's space and source id, from its path. Pure.</summary>
    public static (string Space, string? SourceId)? SpaceOf(string configPath)
    {
        var marker = "/" + GitHubSyncService.ConfigId;
        var at = configPath.IndexOf(marker, StringComparison.Ordinal);
        if (at <= 0)
            return null;
        var space = configPath[..at];
        var rest = configPath[(at + marker.Length)..].Trim('/');
        return (space, rest.Length == 0 ? null : rest);
    }

    /// <summary>Why the source behind <paramref name="config"/> is not updated by a reboot, or null when it is. Pure.</summary>
    public static string? NotAModuleSource(GitHubSyncConfig? config)
    {
        if (config?.RepositoryUrl is not { Length: > 0 })
            return "no repository is configured";
        if (config.Direction == SyncDirection.ExportOnly)
            return "export-only — it never imports";
        if (config.ModuleVersions is null && config.ModuleOutcomes is null)
            return "it has never synced a module-bearing tree (no manifest.lock recorded) — not a module source";
        return null;
    }

    /// <summary>The module judgement an import recorded, as one clause (declines NAMED with their reason). Pure.</summary>
    public static string DescribeModules(GitHubSyncConfig? config)
    {
        var outcomes = config?.ModuleOutcomes;
        if (outcomes is null || outcomes.Count == 0)
            return "no module judgement recorded";
        var synced = outcomes.Count(o => o.Outcome == ModuleSyncOutcomeKind.Synced);
        var unchanged = outcomes.Count(o => o.Outcome == ModuleSyncOutcomeKind.Unchanged);
        var declined = outcomes.Where(o => o.Outcome == ModuleSyncOutcomeKind.Declined).ToImmutableList();
        return $"{synced} module(s) synced, {unchanged} unchanged, {declined.Count} declined"
               + (declined.IsEmpty ? "" : ": " + string.Join("; ", declined.Select(d => $"{d.Module} — {d.Reason}")));
    }

    /// <summary>
    /// Updates every module source of this instance, in path order, and answers one row per sync
    /// source (non-module sources are listed as <see cref="ModuleSourceUpdateOutcome.Skipped"/> with
    /// the reason). Never faults: a listing that cannot be read answers ONE failed row saying so — it
    /// is never "nothing to sync". Cold; emits once.
    /// </summary>
    /// <param name="hub">A hub of the mesh; it must outlive the imports (the request's own hub does).</param>
    public static IObservable<ImmutableList<ModuleSourceUpdateRow>> UpdateAll(IMessageHub hub) =>
        UpdateAllWithin(hub, ActivityBudget);

    /// <summary>
    /// <see cref="UpdateAll"/> with an explicit per-source <paramref name="activityBudget"/>
    /// — the bound a test drives without waiting <see cref="ActivityBudget"/>.
    /// </summary>
    /// <param name="hub">A hub of the mesh; it must outlive the imports (the request's own hub does).</param>
    /// <param name="activityBudget">How long one source's import may run before its row reads Failed.</param>
    public static IObservable<ImmutableList<ModuleSourceUpdateRow>> UpdateAllWithin(IMessageHub hub, TimeSpan activityBudget)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    MeshWideQuery.OfType(GitHubSyncService.ConfigNodeType)))
                .Take(1)
                .Timeout(ListingBudget))
            .Select(change => change.Items
                .Select(n => n.Path)
                .Where(p => SpaceOf(p) is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToImmutableList())
            .SelectMany(paths => paths.ToObservable()
                // A path that names no space never reaches One: the parse is matched HERE, once,
                // so One receives the parsed parts and has nothing left to assume.
                .Select(path => SpaceOf(path) is { } parsed
                    ? One(hub, access, path, parsed.Space, parsed.SourceId, activityBudget)
                    : Observable.Empty<ModuleSourceUpdateRow>())
                .Concat()
                .ToList()
                .Select(rows => rows.ToImmutableList()))
            .Catch((Exception ex) => Observable.Return(ImmutableList.Create(new ModuleSourceUpdateRow(
                GitHubSyncService.ConfigNodeType, "", null, ModuleSourceUpdateOutcome.Failed,
                $"the sync sources could not be listed ({ex.GetType().Name}: {ex.Message}) — NOTHING was synced, "
                + "and this is not a statement that there is nothing to sync"))));
    }

    private static IObservable<ModuleSourceUpdateRow> One(IMessageHub hub, AccessService access, string configPath,
        string space, string? sourceId, TimeSpan activityBudget)
    {
        ModuleSourceUpdateRow Row(string outcome, string detail) => new(configPath, space, sourceId, outcome, detail);
        IObservable<GitHubSyncConfig?> ReadConfig() =>
            // CQRS: the config's CONTENT is read from its own node stream, never from the listing.
            access.RunAsSystem(() => hub.GetMeshNodeStream(configPath).Take(1).Timeout(ListingBudget))
                .Select(n => n.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions));

        return ReadConfig()
            .SelectMany(config => NotAModuleSource(config) is { } why
                ? Observable.Return(Row(ModuleSourceUpdateOutcome.Skipped, why))
                : Import(hub, access, space, sourceId, activityBudget)
                    .SelectMany(ended => ended.Finished
                        ? ReadConfig().Select(after =>
                        {
                            var log = ended.Log!;
                            var commit = after?.LastSyncCommitSha is { Length: > 8 } sha ? sha[..8] : after?.LastSyncCommitSha ?? "?";
                            var tail = log.Messages.LastOrDefault(m => m.LogLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)?.Message;
                            var detail = $"{log.Status}: {after?.LastSyncOutcome ?? "no outcome recorded"} at {commit}; {DescribeModules(after)}"
                                         + (tail is null ? "" : $"; last notice: {tail}") + $" (activity {ended.Activity})";
                            return log.Status.IsError()
                                ? Row(ModuleSourceUpdateOutcome.Failed, detail)
                                : Row(ModuleSourceUpdateOutcome.Updated, detail);
                        })
                        : Observable.Return(Row(ModuleSourceUpdateOutcome.Failed,
                            $"the import did not finish within {Budget(activityBudget)} (activity {ended.Activity}) — "
                            + "its cancellation was requested; its outcome is NOT established"))))
            .Catch((Exception ex) => Observable.Return(Row(ModuleSourceUpdateOutcome.Failed,
                $"the update could not run ({ex.GetType().Name}: {ex.Message})")));
    }

    /// <summary>How one import ended for the row: its terminal log, or <c>Finished = false</c> when the
    /// budget ran out first.</summary>
    private sealed record ImportEnd(string Activity, bool Finished, ActivityLog? Log);

    /// <summary>
    /// 🚨 Runs one source's import and answers how it ended — bounded by <paramref name="activityBudget"/>
    /// from the moment its ACTIVITY IS CREATED (MeshWeaver#6625).
    ///
    /// <para><see cref="ActivityRunner.RunActivity(IMessageHub, string, string, LogMessage, Func{ActivityContext, IObservable{System.Reactive.Unit}}, Action{string})"/>
    /// emits the activity path only when the run is TERMINAL, so a budget composed after that
    /// emission bounded nothing: an import that never finished held the reboot's Sync step — and with
    /// it the request, non-terminal, suppressing every pod's self-reboot — indefinitely. The path is
    /// taken from <c>onActivityCreated</c> instead, and the budget runs against the activity node's
    /// own terminal status. When it runs out the import's cancellation is requested through the
    /// Activity Control Plane (<see cref="HubActivityExtensions.CancelActivity"/>) and the row says the
    /// outcome is NOT established. The import's own subscription is left to end on its own terms —
    /// cancelled, failed or finished — never cut, so its terminal status is still written.</para>
    /// </summary>
    private static IObservable<ImportEnd> Import(
        IMessageHub hub, AccessService access, string space, string? sourceId, TimeSpan activityBudget) =>
        Observable.Create<ImportEnd>(observer =>
        {
            var created = new AsyncSubject<string>();
            var watch = created
                .SelectMany(activity => access.RunAsSystem(() => hub.GetMeshNodeStream(activity))
                    .Select(n => n.ContentAs<ActivityLog>(hub.JsonSerializerOptions))
                    .OfType<ActivityLog>()
                    .Where(log => log.Status.IsTerminal())
                    .Take(1)
                    .Select(log => new ImportEnd(activity, true, log))
                    .Timeout(activityBudget)
                    .Catch((TimeoutException _) =>
                    {
                        hub.CancelActivity(activity);
                        return Observable.Return(new ImportEnd(activity, false, null));
                    }))
                .Subscribe(observer);
            // The import runs for its own lifetime; disposing the row's subscription never cuts it.
            access.RunAsSystem(() => hub.UpdateToLatestFromGitHub(space, WellKnownUsers.System,
                    onActivityCreated: path =>
                    {
                        created.OnNext(path);
                        created.OnCompleted();
                    },
                    sourceId: sourceId))
                .Subscribe(
                    _ => { },
                    // A trigger refused BEFORE any activity exists (no config, no authority) is the
                    // row's own failure; after the activity exists its fault is on the activity node.
                    ex => created.OnError(ex),
                    () => { });
            return watch;
        });

    private static string Budget(TimeSpan budget) =>
        budget.TotalMinutes >= 1 ? $"{budget.TotalMinutes:0} min" : $"{budget.TotalSeconds:0} s";
}
