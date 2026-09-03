using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An "Update available" reminder is a STATE, not an event — a second reconcile pass over the
/// same pending update must leave ONE notification node, not two</b>
/// (Systemorph/MeshWeaver#3213).
///
/// <para>Measured on memex.meshweaver.cloud on 2026-09-03: <b>124 of the newest 200</b> notification
/// rows were this one emitter, two packages producing four rows each per day and the same shape all
/// the way back through the window. <see cref="PackageUpdateReconciler"/> re-decides on every poll,
/// and nothing about a reminder was idempotent.</para>
///
/// <para><b>Two independent halves, and fixing either alone still duplicates</b> — so this fixture
/// exercises both, each with its own arm that fails if that half is missing:</para>
/// <list type="number">
///   <item><b>A silence gate that can actually become true.</b> The content-identity gate asks "is
///     the candidate installed?", which is self-silencing on the <c>AutoUpdate</c> path and
///     UNSATISFIABLE on the reminder path — nothing is installed there, by design, so it evaluates
///     false on every subsequent poll forever. Arm 2 below runs an IDENTICAL second pass; without
///     <see cref="PackageManifest.NotifiedModuleVersion"/> it writes a second row.</item>
///   <item><b>A deterministic identity to be idempotent on.</b> Every notification used to mint a
///     fresh GUID, so a repeat had nothing to collide with. Arm 3 puts the marker back to a stale
///     value — the shape a failed marker write, or a second reconcile entry point racing the first,
///     actually leaves behind — and re-runs: the notify path executes end to end (proved by the
///     marker being rewritten) and still lands on the SAME node.</item>
/// </list>
///
/// <para>Arm 4 is the falsifier the suppression needs: a genuinely NEW candidate version must still
/// raise its own unread bell. A gate that silenced that would pass arms 2 and 3 while breaking the
/// feature, and it is also what bounds the negative assertions — once the listing shows the v3
/// reminder, anything an earlier pass wrote is already committed and would be counted.</para>
/// </summary>
public class PackageUpdateReminderIdempotenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PackageId = "UpdateReminderIdempotencePkg";
    private const string PackageName = "Update Reminder Idempotence Package";
    private const string InstalledModuleVersion = "mv-installed-3213";
    private const string CandidateModuleVersion = "mv-candidate-3213";
    private const string NextCandidateModuleVersion = "mv-candidate-3213-next";

    /// <summary>A marker value that is neither candidate — what a marker write that never landed,
    /// or one left by some other candidate, looks like to the gate.</summary>
    private const string StaleMarker = "mv-marker-from-some-other-pass";

    private const string RegistryUrl = "http://registry.reminder-idempotence.test";
    private const string Token = "mwi_reminder_idempotence_test";

    private static string RecordPath => $"{PackageInstaller.InstalledPartition}/{PackageId}";

    /// <summary>The negative control: the reminder path must not fetch a single file. Every request
    /// this handler ever sees is a reconcile that took the APPLY branch it had no business taking.</summary>
    private readonly CountingRegistry registry = new();

    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new CountingClientFactory(registry)));

    [Fact]
    public async Task ASecondReconcileOverTheSamePendingUpdate_LeavesExactlyOneNotification()
    {
        await SeedInstallRecord();

        var source = new RegistryPackageSource(Mesh, RegistryUrl, Token);
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(PackageUpdateReminderIdempotenceTest));

        // ── 1. The first pass tells the user, once ──────────────────────────────────────────────
        await Reconcile(source, CandidateModuleVersion, "Served by registry 'first pass'", logger);

        var first = await Reminders()
            .Where(ns => ns.Count == 1)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        var reminderPath = first[0].Path;
        var reminder = first[0].ContentAs<Notification>(Json);
        Assert.NotNull(reminder);
        Assert.StartsWith("Update available", reminder!.Title, StringComparison.Ordinal);
        Assert.Contains(PackageName, reminder.Title);
        Assert.False(reminder.IsRead);

        // The gate the reminder path now owns: "have I already told them about THIS candidate?".
        // Writing it is what makes the gate become TRUE — which is precisely what comparing the
        // record's installed version against the candidate could never do here.
        await AwaitMarker(CandidateModuleVersion);

        // ── 2. HALF ONE — an identical second pass says nothing at all ──────────────────────────
        // 🚨 Counting rows CANNOT test this half, and asserting only the count is how a fixture
        // passes having checked nothing: with the deterministic identity in place, a second pass
        // that is NOT suppressed still lands on the SAME node, so the count stays 1 either way.
        // What separates "suppressed" from "absorbed" is what the user sees — so dismiss the bell
        // first. A pass that re-raises an unchanged reminder un-reads it, which is the defect a
        // reader of the bell actually experiences four times a day.
        await MarkRead(reminderPath);
        await AwaitRead(reminderPath, true);

        // This is the pass that produced a new row per poll, forever. Its own completion is the
        // bound: the notification write, if there were one, sits inside the awaited chain.
        await Reconcile(source, CandidateModuleVersion, "Served by registry 'second pass'", logger);

        var afterSecond = await Reminders().FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Single(afterSecond);
        Assert.Equal(reminderPath, afterSecond[0].Path);
        var stillDismissed = afterSecond[0].ContentAs<Notification>(Json);
        Assert.NotNull(stillDismissed);
        Assert.True(stillDismissed!.IsRead,
            "the second pass re-raised a reminder the user had already dismissed — the reminder "
            + "path's silence gate did not suppress it");
        Assert.Equal(reminder.CreatedAt, stillDismissed.CreatedAt);

        // ── 3. HALF TWO — a pass that DOES reach the notify path still lands on the same node ───
        // The marker is put back to a value the gate cannot match, so guard one steps aside and the
        // notify path runs for real. Only the deterministic identity keeps this from adding a row.
        await SetMarker(StaleMarker);
        await AwaitMarker(StaleMarker);

        await Reconcile(source, CandidateModuleVersion, "Served by registry 'third pass'", logger);

        // Positive signal that this pass executed the notify path end to end — not that it was
        // skipped, which would make the assertion below vacuous.
        await AwaitMarker(CandidateModuleVersion);

        var afterThird = await Reminders().FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Single(afterThird);
        Assert.Equal(reminderPath, afterThird[0].Path);

        // The upsert REFRESHES the row, so the reminder reads as new again. That is the honest
        // answer to "this condition is still true and I have no record of having told you", and it
        // is precisely why guard one has to exist: on its own, guard two would do this every poll.
        var refreshed = afterThird[0].ContentAs<Notification>(Json);
        Assert.NotNull(refreshed);
        Assert.False(refreshed!.IsRead);

        // ── 4. A genuinely NEW candidate is still worth telling them about ──────────────────────
        // Suppression keyed too coarsely would swallow this, and every arm above would still pass.
        await Reconcile(source, NextCandidateModuleVersion, "Served by registry 'new build'", logger);

        var afterNew = await Reminders()
            .Where(ns => ns.Any(n => n.Path != reminderPath))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        Assert.Equal(2, afterNew.Count);
        var newReminder = afterNew.Single(n => n.Path != reminderPath).ContentAs<Notification>(Json);
        Assert.NotNull(newReminder);
        Assert.StartsWith("Update available", newReminder!.Title, StringComparison.Ordinal);
        Assert.False(newReminder.IsRead);
        await AwaitMarker(NextCandidateModuleVersion);

        // The listing now contains the newest write, so every earlier one is committed too: two
        // candidates, two reminders — not one row per pass.
        Assert.Equal(2, (await Reminders().FirstAsync().Timeout(TestTimeouts.Convergence).Await()).Count);

        // …and the reminder path fetched nothing. Four reconciles, zero registry round-trips.
        Assert.Equal(0, registry.Attempts);
    }

    /// <summary>One reconcile pass over one candidate — the exact call every entry point makes
    /// (the boot reconcile, a feed read draining a deferral, a build webhook), awaited to
    /// completion so the pass's own writes are done before the assertion reads.</summary>
    private async Task Reconcile(
        IPackageSource source, string candidate, string provenance, ILogger logger)
        => await PackageUpdateReconciler
            .ReconcileInstalled(Mesh, source, "HEAD", [Candidate(candidate)], provenance, logger)
            .Timeout(TestTimeouts.Convergence).Await();

    private static PackageManifest Candidate(string moduleVersion) => new()
    {
        Id = PackageId,
        Name = PackageName,
        Version = "1.0.1",
        ModuleVersion = moduleVersion,
        TargetPartition = PackageId,
    };

    private async Task SeedInstallRecord()
    {
        var manifest = new PackageManifest
        {
            Id = PackageId,
            Name = PackageName,
            Version = "1.0.0",
            ModuleVersion = InstalledModuleVersion,
            TargetPartition = PackageId,
            // The reminder path — the one whose gate could never become true.
            AutoUpdate = false,
        };
        var record = MeshNode.FromPath(RecordPath) with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = manifest.Name,
            State = MeshNodeState.Active,
            Content = manifest,
        };
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record))
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private async Task SetMarker(string value)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => Mesh.GetMeshNodeStream(RecordPath)
                .Update<PackageManifest>(current => current with { NotifiedModuleVersion = value }))
            .Timeout(TestTimeouts.Convergence).Await();
    }

    /// <summary>The user dismissing the bell — the same field the bell list flips on click.</summary>
    private async Task MarkRead(string notificationPath)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access
            .RunAsSystem(() => Mesh.GetMeshNodeStream(notificationPath)
                .Update<Notification>(current => current with { IsRead = true }))
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private async Task AwaitRead(string notificationPath, bool expected) =>
        await Mesh.GetMeshNodeStream(notificationPath)
            .Select(n => n.ContentAs<Notification>(Json)?.IsRead)
            .Where(v => v == expected)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

    private async Task AwaitMarker(string expected) =>
        await Mesh.GetMeshNodeStream(RecordPath)
            .Select(n => n.ContentAs<PackageManifest>(Json)?.NotifiedModuleVersion)
            .Where(v => string.Equals(v, expected, StringComparison.Ordinal))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();

    /// <summary>The reminder satellites of the install record, read LIVE through a children query
    /// (a query never storms on a parent that does not exist yet, and it re-emits on every write).</summary>
    private IObservable<IReadOnlyList<MeshNode>> Reminders() =>
        Mesh.GetWorkspace()
            .GetQuery("notif|pkg|reminder-idempotence",
                $"path:{RecordPath}/{NotificationService.SatelliteSegment} scope:children nodeType:Notification")
            .Select(ns => (IReadOnlyList<MeshNode>)(ns ?? []).ToList());

    /// <summary>Answers nothing and counts every attempt — the reminder path must never reach it.</summary>
    private sealed class CountingRegistry : HttpMessageHandler
    {
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        // HttpMessageHandler's contract is Task-shaped; nothing here bridges an observable.
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"error\":\"the reminder path must not fetch\"}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CountingClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
