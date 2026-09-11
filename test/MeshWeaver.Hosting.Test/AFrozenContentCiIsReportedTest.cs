using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The publish gate's one failure mode is a SILENT freeze, and this is the control that it is
/// not silent.</b>
///
/// <para>#3978 made the repository's own content CI the publish signal — a green run of any other
/// workflow no longer records a build or authorises an import. That is correct, and it has exactly
/// one way to go wrong: the platform expects a workflow path the repository does not use. Then every
/// delivery is refused, GitHub is answered 200, every Space that syncs the repository silently stops
/// advancing, and nothing anywhere reports it. That is MeshWeaver.Plugins#1194 verbatim — 38 hours
/// behind a merged main, every delivery 200 OK — and the whole reason design question 2 of #3978
/// says a fail-closed gate MUST carry a positive signal.</para>
///
/// <para><b>Both directions, because a signal that always fires is noise and noise is how a real
/// line gets missed.</b> Core refuses roughly 200 green runs a day and the <c>*/10</c> PR-updater
/// cron alone is 144 a day in three satellites; a Warning on each would bury the one that matters.
/// So the assertions are paired: a repository whose content CI has NEVER been accepted gets the
/// Warning, and the same repository — same refused workflow, same payload shape — gets NO Warning
/// once its content CI has published, which is what proves the report discriminates rather than
/// fires unconditionally.</para>
/// </summary>
public class AFrozenContentCiIsReportedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SyncedRepo = "test/frozen-content-ci";
    private const string SyncedRepoUrl = $"https://github.com/{SyncedRepo}";
    private const string ForeignRepo = "test/nobody-syncs-this";

    /// <summary>The path the platform presumes for a repository nothing declares.</summary>
    private const string ContentCi = ".github/workflows/ci.yml";

    /// <summary>The live #3978 shape: a cron that checks out nothing and builds nothing.</summary>
    private const string PrUpdater = ".github/workflows/auto-update-green-prs.yml";

    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> logs = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddLogging(logging =>
                    logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logs)));
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    private GitHubWebhookProcessor Webhooks =>
        Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();

    // 120_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, and
    // the inner waits carry the adaptive bound. This outer one only has to stop a WEDGE.
    [Fact(Timeout = 120_000)]
    public async Task ARefusedWorkflow_IsReportedWhenItFreezesASyncedSpace_AndNotOtherwise()
    {
        var space = "Frozen" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Frozen content CI",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await();

        await Sync.SaveConfig(space, SyncedRepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false)
            .Timeout(TestTimeouts.Convergence).Await();

        // ── 1. The repository NOTHING syncs: refusing its green run withheld nothing ──────────
        //
        // Asserted FIRST and from the same code path, because it is what makes the Warning below a
        // discriminator instead of a constant. Without it, a report that fired on every refusal
        // would pass this test's positive arm just as well.
        (await Deliver(ForeignRepo, PrUpdater, "aaaa1111aaaa1111aaaa1111aaaa1111aaaa1111"))
            .Should().Be(0, "a refused workflow never records, whoever the repository is");
        Warnings().Should().NotContain(
            line => line.Contains(ForeignRepo, StringComparison.Ordinal),
            "no sync config targets this repository, so the refusal froze nothing and a Warning "
            + "here would be pure noise — hundreds a day, which is how the one that matters is lost");

        // ── 2. A SYNCED repository whose content CI has never been accepted: the Warning ──────
        (await Deliver(SyncedRepo, PrUpdater, "bbbb2222bbbb2222bbbb2222bbbb2222bbbb2222"))
            .Should().Be(0, "the PR updater checks out nothing and builds nothing (#3978)");

        var frozen = Warnings()
            .Where(line => line.Contains(SyncedRepo, StringComparison.Ordinal))
            .ToList();
        frozen.Should().ContainSingle(
            "a green default-branch run refused for a SYNCED repository whose content CI has never "
            + "been accepted is the silent-freeze shape, and it must not be silent");

        var reported = frozen[0];
        reported.Should().Contain("COULD NOT DETERMINE THIS REPOSITORY'S CONTENT CI",
            "'I could not determine this repository's content CI' and 'its content CI was green' are "
            + "different answers, and collapsing them is the defect #3978 fixed one level up");
        reported.Should().Contain(ContentCi,
            "the line must name the path that IS expected, or the reader cannot act on it");
        reported.Should().Contain(PrUpdater,
            "…and the path that actually arrived, which is the other half of the comparison");
        reported.Should().Contain(space,
            "naming the frozen Space is what turns a log line into an outage report");
        reported.Should().Contain("FROZEN",
            "the consequence, not just the decision: every delivery is answered 200 and nothing "
            + "else will report this (MeshWeaver.Plugins#1194)");
        reported.Should().Contain(GitHubContentWorkflowOptions.ConfigSection,
            "a report that does not say how to fix it leaves the reader where #1194 left them");
        Output.WriteLine(reported);

        // ── 3. Once the content CI HAS published, the same refusal is routine ─────────────────
        //
        // The record is seeded rather than produced by a real delivery: this file is about the
        // REPORT, and driving an import here would pull the git transport in for nothing. That the
        // real gate still publishes — under push, repository_dispatch AND schedule — is the subject
        // of BuildTriggeredSyncPinsTheBuiltCommitTest.
        var recordPath = BuildCompletion.PathFor("test", "frozen-content-ci");
        var slash = recordPath.LastIndexOf('/');
        await NodeFactory.CreateNode(new MeshNode(recordPath[(slash + 1)..], recordPath[..slash])
        {
            NodeType = BuildCompletion.NodeType,
            Name = $"{SyncedRepo} build",
            State = MeshNodeState.Active,
            Content = new BuildCompletion
            {
                RepositoryUrl = SyncedRepoUrl,
                Branch = "main",
                HeadSha = "cccc3333cccc3333cccc3333cccc3333cccc3333",
                WorkflowName = "Content CI",
                WorkflowPath = ContentCi,
                Conclusion = "success",
            },
        }).Timeout(TestTimeouts.Convergence).Await();

        (await Deliver(SyncedRepo, PrUpdater, "dddd4444dddd4444dddd4444dddd4444dddd4444"))
            .Should().Be(0, "still not a publish signal — nothing about the gate changed");

        Warnings()
            .Where(line => line.Contains(SyncedRepo, StringComparison.Ordinal))
            .Should().ContainSingle(
                "the content CI has now published AT THE EXPECTED PATH, so this refusal is some "
                + "other workflow finishing green — a second report here would be the noise that "
                + "buries the one in arm 2; the single line is still arm 2's");
    }

    /// <summary>
    /// Delivers one green, completed, default-branch <c>workflow_run</c> and returns how many sync
    /// sources it triggered. The webhook request is ANONYMOUS — its authorization is the verified
    /// HMAC signature — so every ambient identity is dropped first and the processor's own System
    /// impersonation is what carries the reads and the write, exactly as on an access-gated portal.
    /// </summary>
    private async Task<int> Deliver(string repoFullName, string workflowPath, string headSha)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        try
        {
            return await Webhooks
                .Process("workflow_run", Payload(repoFullName, workflowPath, headSha))
                .Timeout(TestTimeouts.Convergence).Await();
        }
        finally
        {
            accessService.SetHostIdentity(
                new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
    }

    private static JsonElement Payload(string repoFullName, string workflowPath, string headSha)
        => JsonDocument.Parse($$"""
        {
          "action": "completed",
          "repository": { "full_name": "{{repoFullName}}", "default_branch": "main" },
          "workflow_run": {
            "conclusion": "success", "head_branch": "main", "head_sha": "{{headSha}}",
            "id": 34061098155, "run_number": 2026, "name": "Some Workflow", "event": "schedule",
            "path": "{{workflowPath}}",
            "updated_at": "2026-09-11T06:00:00Z"
          }
        }
        """).RootElement;

    /// <summary>Every Warning the webhook processor has emitted so far.</summary>
    private System.Collections.Generic.List<string> Warnings() => logs
        .Where(r => r.Level >= LogLevel.Warning
                    && r.Category.Contains(nameof(GitHubWebhookProcessor), StringComparison.Ordinal))
        .Select(r => r.Message)
        .ToList();

    /// <summary>Captures every record, with its category, into the owning test's instance queue.</summary>
    private sealed class QueueLoggerProvider(
        ConcurrentQueue<(string Category, LogLevel Level, string Message)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(categoryName, sink);

        public void Dispose() { }

        private sealed class QueueLogger(
            string category,
            ConcurrentQueue<(string Category, LogLevel Level, string Message)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
