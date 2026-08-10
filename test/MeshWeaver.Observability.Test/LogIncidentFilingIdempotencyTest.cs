using System.Collections.Immutable;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// <b>One fault, one ticket — for the whole life of the fault, not just its first sighting.</b>
///
/// <para>The watcher's first live run (2026-08-10 12:03–12:17Z) opened 37 issues for ~12 defects.
/// The fingerprint held perfectly — 602 occurrences of <c>ROUTER_TRAFFIC</c> stayed ONE incident —
/// but the FILING duplicated, two ways, and these tests pin both:</para>
/// <list type="number">
///   <item><description><b>Re-filing on recurrence.</b> A recurrence parked the incident at
///   <c>Failed</c>, the ingest path re-triaged it, the agent drafted again and asked to
///   <c>File</c>, and nothing looked at the issue that already existed —
///   <c>ROUTER_TRAFFIC</c> was filed eight times in seven minutes.</description></item>
///   <item><description><b>Same-second doubles.</b> #1124/#1125 and #1111/#1112 were each opened
///   within one second: the live incident query is eventually consistent and re-emits the
///   pre-write snapshot, so two work items carried one <c>File</c> request and neither claimed the
///   incident before opening the issue.</description></item>
/// </list>
///
/// <para>Every timing here comes from the incident's own <c>LastSeen</c> — the same clock the
/// recurrence policy reads — so there is no wall-clock dependency, no sleep and no scheduler to
/// pump: the comment window is crossed by moving the data, not by waiting.</para>
///
/// <para>The mesh registers <c>AddLogWatch()</c> with NO destination configured, so the
/// DI-hosted control plane logs "idle" and never subscribes to the incident query. The plane under
/// test is constructed here with its own options and a fake GitHub, and driven one work item at a
/// time through <c>RunRequest</c> — exactly what the queue does, minus the emission timing that
/// cannot be reproduced on demand.</para>
/// </summary>
public class LogIncidentFilingIdempotencyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Fingerprint = "routertraffic0001";
    private static readonly string IncidentPath = LogIncidentNodeType.IncidentPath(Fingerprint);
    private static readonly DateTimeOffset T0 = new(2026, 8, 10, 12, 3, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>The deployment policy the plane under test runs with.</summary>
    private static readonly LogWatchOptions Policy = new()
    {
        DefaultRepository = "Systemorph/MeshWeaver",
        CommentInterval = TimeSpan.FromHours(6),
        ReopenOnRecurrence = true,
    };

    private readonly FakeIssueApi github = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddLogWatch();

    // ══════════════════════════════════════════════════════════════════════════
    //  The two duplication mechanisms
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 120_000)]
    public async Task TwoFileRequestsForOneIncident_OpenExactlyOneIssue()
    {
        // ONE snapshot, TWO work items — the same-second double, verbatim: the query re-emitted the
        // pre-write state after the in-process guard had been released.
        var stale = await Drafted();
        var plane = ControlPlane();

        await plane.RunRequest(IncidentPath, stale, LogIncidentRequest.File).Timeout(Bound).ToTask();
        await plane.RunRequest(IncidentPath, stale, LogIncidentRequest.File).Timeout(Bound).ToTask();

        github.Created.Should().Be(1,
            "the second work item carries a request the first already consumed — the claim is taken "
            + "on the live node BEFORE the issue is opened, so only one worker can file");
        github.Comments.Should().Be(0, "nothing recurred — the second request was a duplicate, not news");

        var incident = await Read(i => i.Status == LogIncidentStatus.Filed);
        incident.IssueNumber.Should().Be(1);
        incident.IssueUrl.Should().NotBeNull("the issue link is what every later recurrence keys off");
        incident.Repository.Should().Be("Systemorph/MeshWeaver");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARecurrenceAskingToFileAgain_LandsOnTheExistingIssue()
    {
        await FileOnce();

        // The production chain, verbatim: the fault comes back, the incident is parked at Failed
        // (a comment that errored, or the stranded-triage reconcile), the ingest path re-triages,
        // and the agent's fresh draft asks to File again.
        var recurred = await Recur(current => current with
        {
            Status = LogIncidentStatus.Failed,
            Error = "Triage finished without a draft.",
            RequestedStatus = LogIncidentRequest.File,
            Occurrences = 602,
            LastSeen = T0.AddMinutes(10),
        }, until: i => i.RequestedStatus == LogIncidentRequest.File);

        await ControlPlane().RunRequest(IncidentPath, recurred, LogIncidentRequest.File)
            .Timeout(Bound).ToTask();

        github.Created.Should().Be(1,
            "a fault that comes back belongs on the ticket it already has — this is the eight-issues-"
            + "in-seven-minutes chain, and the issue link now outranks the status");
        github.Comments.Should().Be(1, "the recurrence is reported once, on the existing issue");
        github.LastComment.Should().Contain("602", "the comment carries the new occurrence count");

        var incident = await Read(i => i.LastCommentedAt is not null);
        incident.IssueNumber.Should().Be(1);
        incident.Status.Should().Be(LogIncidentStatus.Filed,
            "a comment that lands proves the issue exists — the stale Failed is what re-triaged it");
        incident.Error.Should().BeNull();
        incident.OccurrencesAtLastComment.Should().Be(602);
    }

    [Fact(Timeout = 120_000)]
    public async Task AFurtherRecurrenceInsideTheCommentWindow_SaysNothingAtAll()
    {
        await FileOnce();
        var first = await Recur(current => current with
        {
            RequestedStatus = LogIncidentRequest.File,
            Occurrences = 602,
            LastSeen = T0.AddMinutes(10),
        }, until: i => i.RequestedStatus == LogIncidentRequest.File);
        await ControlPlane().RunRequest(IncidentPath, first, LogIncidentRequest.File)
            .Timeout(Bound).ToTask();
        await Read(i => i.LastCommentedAt is not null);

        // Twenty minutes later, still firing. The comment window is six hours.
        var again = await Recur(current => current with
        {
            RequestedStatus = LogIncidentRequest.File,
            Occurrences = 1200,
            LastSeen = T0.AddMinutes(30),
        }, until: i => i.RequestedStatus == LogIncidentRequest.File);
        await ControlPlane().RunRequest(IncidentPath, again, LogIncidentRequest.File)
            .Timeout(Bound).ToTask();

        github.Created.Should().Be(1);
        github.Comments.Should().Be(1,
            "a continuously-firing fault gets ONE update per CommentInterval, whatever asked for it — "
            + "a File request that arrives on a ticketed incident obeys the same bound as a Comment");

        var incident = await Read(i => i.RequestedStatus == LogIncidentRequest.None);
        incident.Status.Should().Be(LogIncidentStatus.Filed, "silence is not failure");
    }

    [Fact(Timeout = 120_000)]
    public async Task ARecurrenceAfterTheIssueWasClosed_ReopensIt()
    {
        await FileOnce();
        github.Close(1);

        var recurred = await Recur(current => current with
        {
            RequestedStatus = LogIncidentRequest.Comment,
            Occurrences = 900,
            LastSeen = T0.AddHours(9),
        }, until: i => i.RequestedStatus == LogIncidentRequest.Comment);

        await ControlPlane().RunRequest(IncidentPath, recurred, LogIncidentRequest.Comment)
            .Timeout(Bound).ToTask();

        github.Created.Should().Be(1, "a regression re-surfaces on the original ticket");
        github.State(1).Should().Be(GitHubIssueState.Open,
            "a defect that returns after someone closed its ticket is exactly what should notify");
        github.Comments.Should().Be(1);
        github.LastComment.Should().Contain("Reopened");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The claim rule itself (no mesh — the whole policy is pure)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AConsumedRequest_IsNeverPerformedTwice()
    {
        // What the second work item of a same-second double sees once the first has claimed.
        var claimed = new LogIncident { Fingerprint = Fingerprint, RequestedStatus = LogIncidentRequest.None };

        LogIncidentControlPlane.ClaimRequest(claimed, LogIncidentRequest.File, Policy)
            .Granted.Should().Be(LogIncidentRequest.None);
        LogIncidentControlPlane.ClaimRequest(claimed, LogIncidentRequest.Triage, Policy)
            .Granted.Should().Be(LogIncidentRequest.None,
                "a duplicated Triage starts a second agent round — the same defect, paid for in model budget");
    }

    [Fact]
    public void AGrantedClaim_StampsTheInFlightStatusAndClearsTheRequest()
    {
        var claim = LogIncidentControlPlane.ClaimRequest(
            new LogIncident { RequestedStatus = LogIncidentRequest.File, Error = "an earlier failure" },
            LogIncidentRequest.File, Policy);

        claim.Granted.Should().Be(LogIncidentRequest.File);
        claim.Incident.Status.Should().Be(LogIncidentStatus.Filing);
        claim.Incident.RequestedStatus.Should().Be(LogIncidentRequest.None,
            "clearing the request IS the claim — it is what a second worker reads");
        claim.Incident.Error.Should().BeNull();
    }

    [Fact]
    public void AFileRequestOnATicketedIncident_IsGrantedAsAComment()
    {
        var ticketed = new LogIncident
        {
            Status = LogIncidentStatus.Failed,
            RequestedStatus = LogIncidentRequest.File,
            IssueNumber = 1113,
            Repository = "Systemorph/MeshWeaver",
            LastSeen = T0,
        };

        var claim = LogIncidentControlPlane.ClaimRequest(ticketed, LogIncidentRequest.File, Policy);

        claim.Granted.Should().Be(LogIncidentRequest.Comment);
        claim.Incident.Status.Should().Be(LogIncidentStatus.Filed);
        claim.Incident.IssueNumber.Should().Be(1113, "the claim never drops the link it is protecting");
    }

    [Fact]
    public void AFileRequestInsideTheCommentWindow_IsGrantedNothing()
        => LogIncidentControlPlane.ClaimRequest(
                new LogIncident
                {
                    Status = LogIncidentStatus.Filed,
                    RequestedStatus = LogIncidentRequest.File,
                    IssueNumber = 1113,
                    LastCommentedAt = T0,
                    LastSeen = T0.AddMinutes(5),
                },
                LogIncidentRequest.File, Policy)
            .Granted.Should().Be(LogIncidentRequest.None,
                "a fault firing continuously must not turn its own issue into a feed");

    // ══════════════════════════════════════════════════════════════════════════
    //  Harness
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The plane under test: its own policy, its own filer, its own fake GitHub.</summary>
    private LogIncidentControlPlane ControlPlane()
    {
        var options = Options.Create(Policy);
        return new LogIncidentControlPlane(Mesh, new LogIncidentFiler(github, AppTokens(), options), options);
    }

    /// <summary>
    /// A REAL <see cref="GitHubAppTokenService"/> against a fake GitHub App API — the filer refuses
    /// to run without the App identity, and faking the identity rather than the service keeps the
    /// production credential path in the test.
    /// </summary>
    private GitHubAppTokenService AppTokens() => new(
        Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>(),
        Options.Create(new GitHubAppOptions
        {
            ClientId = "Iv23liLogWatchApp",
            PrivateKey = FakeGitHubApp.TestKeyPem,
            InstallationOwner = "Systemorph",
        }),
        logger: null,
        httpClient: new HttpClient(new FakeGitHubApp.Handler()));

    private LogIncidentIngestService Ingest() => new(Mesh, Options.Create(Policy));

    /// <summary>The incident as it stands when triage has produced a draft and asked to file.</summary>
    private async Task<LogIncident> Drafted()
    {
        await Ingest().Report(new LogIncidentReport
        {
            Fingerprint = Fingerprint,
            Category = "MeshWeaver.Connection.Orleans.OrleansRoutingService",
            Severity = LogSeverity.Error,
            NormalizedMessage = "Router traffic to {address} failed",
            Namespace = "memex",
            Pods = ImmutableList.Create("memex-portal-0"),
            Occurrences = 40,
            FirstSeen = T0,
            LastSeen = T0,
            Samples = ImmutableList.Create(new LogSample(T0, "memex-portal-0", "fail: routing")),
        }).FirstAsync().Timeout(Bound).ToTask();

        await Mesh.UpdateIncident(IncidentPath, current => current with
        {
            Status = LogIncidentStatus.Triaging,
            RequestedStatus = LogIncidentRequest.File,
            Draft = new LogIncidentDraft
            {
                Title = "Orleans router drops traffic to unreachable silos",
                Body = "The routing service retries a dead silo forever.",
            },
        }).FirstAsync().Timeout(Bound).ToTask();

        return await Read(i => i.RequestedStatus == LogIncidentRequest.File);
    }

    /// <summary>Files the incident once — the starting point every recurrence test builds on.</summary>
    private async Task FileOnce()
    {
        var drafted = await Drafted();
        await ControlPlane().RunRequest(IncidentPath, drafted, LogIncidentRequest.File)
            .Timeout(Bound).ToTask();
        await Read(i => i is { Status: LogIncidentStatus.Filed, IssueNumber: not null });
    }

    /// <summary>Applies a recurrence to the live incident and reads back the settled state.</summary>
    private async Task<LogIncident> Recur(
        Func<LogIncident, LogIncident> recurrence, Func<LogIncident, bool> until)
    {
        await Mesh.UpdateIncident(IncidentPath, recurrence).FirstAsync().Timeout(Bound).ToTask();
        return await Read(until);
    }

    /// <summary>
    /// The incident's live content, once it satisfies <paramref name="until"/>. Never a bare
    /// <c>Take(1)</c> after a write — the first emission off a shared handle can still be the
    /// pre-write snapshot.
    /// </summary>
    private Task<LogIncident> Read(Func<LogIncident, bool> until) =>
        Mesh.GetWorkspace().GetMeshNodeStream(IncidentPath)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<LogIncident>(Mesh.JsonSerializerOptions))
            .Where(incident => incident is not null && until(incident))
            .Select(incident => incident!)
            .FirstAsync()
            .Timeout(Bound)
            .ToTask();
}

/// <summary>
/// In-memory GitHub issue API. Counts what was actually opened, commented and reopened — the
/// numbers the duplicate-notification defect is measured in. No network, no statics, no clock.
/// </summary>
internal sealed class FakeIssueApi : IGitHubRepoClient
{
    private readonly object gate = new();
    private readonly Dictionary<int, GitHubIssue> issues = new();
    private int next;
    private int comments;
    private string? lastComment;

    /// <summary>How many issues were opened.</summary>
    public int Created { get { lock (gate) return issues.Count; } }

    /// <summary>How many comments were posted, across all issues.</summary>
    public int Comments { get { lock (gate) return comments; } }

    /// <summary>The body of the most recent comment.</summary>
    public string? LastComment { get { lock (gate) return lastComment; } }

    /// <summary>The current state of an issue.</summary>
    public GitHubIssueState State(int number) { lock (gate) return issues[number].State; }

    /// <summary>Closes an issue, as a human would after fixing the defect.</summary>
    public void Close(int number)
    {
        lock (gate) issues[number] = issues[number] with { State = GitHubIssueState.Closed };
    }

    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
        => Observable.Defer(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (gate)
            {
                var number = ++next;
                var issue = new GitHubIssue
                {
                    Number = number,
                    Title = request.Title,
                    Body = request.Body,
                    State = GitHubIssueState.Open,
                    Labels = request.Labels,
                    Url = $"{request.RepositoryUrl}/issues/{number}",
                };
                issues[number] = issue;
                return Observable.Return(issue);
            }
        });

    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        => Observable.Defer(() =>
        {
            lock (gate) return Observable.Return(issues[number]);
        });

    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
        => Observable.Defer(() =>
        {
            lock (gate)
            {
                var issue = issues[number] with { State = state };
                issues[number] = issue;
                return Observable.Return(issue);
            }
        });

    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
        => Observable.Defer(() =>
        {
            lock (gate)
            {
                comments++;
                lastComment = body;
                return Observable.Return(new GitHubIssueComment(comments, "meshweaver[bot]", body, null, null));
            }
        });

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => throw new NotSupportedException();
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => throw new NotSupportedException();
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => throw new NotSupportedException();
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => throw new NotSupportedException();
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(string repositoryUrl, GitHubIssueState? state, string accessToken) => throw new NotSupportedException();
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(string repositoryUrl, PullRequestStatus? state, string accessToken) => throw new NotSupportedException();
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken) => throw new NotSupportedException();
    public IObservable<GitHubIssueComment> CommentPullRequest(string repositoryUrl, int number, string body, string accessToken) => throw new NotSupportedException();
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => throw new NotSupportedException();
}

/// <summary>The GitHub App API a real <see cref="GitHubAppTokenService"/> mints its token against.</summary>
internal static class FakeGitHubApp
{
    /// <summary>A throwaway RSA key for the App JWT signature.</summary>
    public static readonly string TestKeyPem = CreateKey();

    private static string CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    /// <summary>Installation discovery + token minting, offline and deterministic.</summary>
    public sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/app/installations")
                return Task.FromResult(Json("""[{"id": 42, "account": {"login": "Systemorph"}}]"""));
            if (request.Method == HttpMethod.Post && path.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                return Task.FromResult(Json($$"""{"token": "ghs_logwatch_token", "expires_at": "{{expires}}"}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unexpected {request.Method} {path}"),
            });
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
