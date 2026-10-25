using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The GitHub REST client (<see cref="OctokitGitHubRepoClient"/> over <see cref="GitHubRestApi"/>)
/// against a recorded-answer HTTP handler — a TRANSPORT fake, not a fake of any core interface.
/// Every endpoint the client calls is pinned here by its request shape (method, path, query,
/// headers, JSON body) and by how its answer and its failures map, so the port off Octokit is
/// held to the behaviour the callers relied on.
///
/// <para>Each case asserts the requests the handler actually SAW, not only the result: a client
/// that answered from the wrong endpoint, or never called GitHub at all, would otherwise pass any
/// assertion about what came back. An unrouted request answers 418, so a request this file did not
/// expect fails loudly instead of being served a plausible default.</para>
/// </summary>
public class GitHubRestTransportTest
{
    private const string Repo = "https://github.com/acme/widgets";
    private const string Api = "https://api.github.com/";
    private const string R = "/repos/acme/widgets";
    private static readonly TimeSpan Budget = TestTimeouts.Quick;

    // ── headers & auth ───────────────────────────────────────────────────────

    [Fact]
    public async Task ATokenTravelsAsAnOctokitStyleTokenHeader_WithTheVersionedGitHubHeaders()
    {
        var gh = new FakeGitHub().On("GET", R + "/git/ref/heads/main", Json("""{"ref":"refs/heads/main","object":{"sha":"abc1234"}}"""));
        using var pools = new IoPoolRegistry();

        var sha = await Run(Client(pools, gh).GetHeadSha(Repo, "main", "s3cret"));

        Assert.Equal("abc1234", sha);
        var request = Assert.Single(gh.Requests);
        Assert.Equal("token s3cret", request.Authorization);
        Assert.Equal("MeshWeaver", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal(GitHubRestApi.ApiVersion, request.ApiVersion);
    }

    [Fact]
    public async Task AnEmptyTokenIsAnonymous_NoAuthorizationHeaderAtAll()
    {
        var gh = new FakeGitHub().On("GET", R + "/git/ref/heads/main", Json("""{"object":{"sha":"abc1234"}}"""));
        using var pools = new IoPoolRegistry();

        await Run(Client(pools, gh).GetHeadSha(Repo, "main", ""));

        Assert.Null(Assert.Single(gh.Requests).Authorization);
    }

    // ── redirects ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARenamedRepositoryIsFollowed_WithTheCredential_AndAnswersItsNewName()
    {
        var gh = new FakeGitHub()
            .On("GET", "/repos/acme/old-name", Redirect(HttpStatusCode.MovedPermanently, Api + "repositories/42"))
            .On("GET", "/repositories/42", Json("""{"name":"widgets","owner":{"login":"acme"},"default_branch":"main"}"""));
        using var pools = new IoPoolRegistry();

        var identity = await Run(Client(pools, gh).GetCanonicalRepository("https://github.com/acme/old-name", "tok"));

        Assert.Equal(new RepoIdentity("acme", "widgets"), identity);
        Assert.Equal(2, gh.Requests.Count);
        // 🚨 HttpClient's own redirect following strips Authorization; a private renamed repository
        // would then 404 on the second hop. The transport keeps it on the same host.
        Assert.All(gh.Requests, r => Assert.Equal("token tok", r.Authorization));
    }

    [Fact]
    public async Task ARedirectToAnotherHostDropsTheCredential()
    {
        var gh = new FakeGitHub()
            .On("GET", R, Redirect(HttpStatusCode.TemporaryRedirect, "https://elsewhere.example.com/repos/acme/widgets"))
            .On("GET", "https://elsewhere.example.com/repos/acme/widgets", Json("""{"name":"widgets","owner":{"login":"acme"}}"""));
        using var pools = new IoPoolRegistry();

        await Run(Client(pools, gh).GetCanonicalRepository(Repo, "tok"));

        Assert.Equal("token tok", gh.Requests[0].Authorization);
        Assert.Null(gh.Requests[1].Authorization);
    }

    [Fact]
    public async Task ARedirectLoopFailsAfterOctokitsHopLimit()
    {
        var gh = new FakeGitHub().On("GET", R, Redirect(HttpStatusCode.Found, Api + "repos/acme/widgets"));
        using var pools = new IoPoolRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(Client(pools, gh).GetCanonicalRepository(Repo, "tok")));

        Assert.Equal(GitHubRestApi.MaxRedirects + 1, gh.Requests.Count);
    }

    // ── refs, commits, trees, blobs ──────────────────────────────────────────

    [Fact]
    public async Task ABranchNameKeepsItsSlashes_AndAShaReadsTheCommit()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/git/ref/heads/feat/x", Json("""{"object":{"sha":"1111111"}}"""))
            .On("GET", R + "/git/commits/abcdef1234", Json("""{"sha":"abcdef1234567","tree":{"sha":"t1"}}"""));
        using var pools = new IoPoolRegistry();
        var client = Client(pools, gh);

        Assert.Equal("1111111", await Run(client.GetHeadSha(Repo, "feat/x", "t")));
        Assert.Equal("abcdef1234567", await Run(client.GetHeadSha(Repo, "abcdef1234", "t")));
        Assert.Equal(["GET " + R + "/git/ref/heads/feat/x", "GET " + R + "/git/commits/abcdef1234"],
            gh.Requests.Select(r => r.Line));
    }

    [Fact]
    public async Task AFetchReadsRefCommitRecursiveTreeAndOnlyTheFilteredBlobs_DecodingBase64()
    {
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello\n"));
        var gh = new FakeGitHub()
            .On("GET", R + "/git/ref/heads/main", Json("""{"object":{"sha":"c1"}}"""))
            .On("GET", R + "/git/commits/c1", Json("""{"sha":"c1","tree":{"sha":"t1"}}"""))
            .On("GET", R + "/git/trees/t1?recursive=1", Json("""
                {"sha":"t1","truncated":false,"tree":[
                  {"path":"content/a.md","type":"blob","sha":"b1"},
                  {"path":"content/skip.bin","type":"blob","sha":"b2"},
                  {"path":"content","type":"tree","sha":"t2"},
                  {"path":"other/c.md","type":"blob","sha":"b3"}]}
                """))
            .On("GET", R + "/git/blobs/b1", Json($$"""{"content":"{{text}}","encoding":"base64"}"""));
        using var pools = new IoPoolRegistry();

        var snapshot = await Run(Client(pools, gh).Fetch(Repo, "main", "content", "t", p => p.EndsWith(".md")));

        Assert.Equal("c1", snapshot.CommitSha);
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("a.md", file.Path);
        Assert.Equal("hello\n", file.Content);
        Assert.True(snapshot.ListingIsComplete);
        Assert.DoesNotContain(gh.Requests, r => r.Line.Contains("/git/blobs/b2") || r.Line.Contains("/git/blobs/b3"));
    }

    [Fact]
    public async Task ATruncatedTreeMarksTheListingIncomplete()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/git/commits/abcdef1", Json("""{"sha":"abcdef1","tree":{"sha":"t1"}}"""))
            .On("GET", R + "/git/trees/t1?recursive=1", Json("""{"truncated":true,"tree":[]}"""));
        using var pools = new IoPoolRegistry();

        var snapshot = await Run(Client(pools, gh).Fetch(Repo, "abcdef1", null, "t"));

        Assert.Empty(snapshot.Files);
        Assert.False(snapshot.ListingIsComplete);
    }

    [Fact]
    public async Task APushToAnExistingBranch_WritesBlobsTreeCommitAndMovesTheRefUnforced()
    {
        var gh = new FakeGitHub()
            .On("GET", R, Json("""{"name":"widgets","owner":{"login":"acme"}}"""))
            .On("GET", R + "/git/ref/heads/main", Json("""{"object":{"sha":"p1"}}"""))
            .On("GET", R + "/git/commits/p1", Json("""{"sha":"p1","tree":{"sha":"t0"}}"""))
            .On("GET", R + "/git/trees/t0?recursive=1", Json("""
                {"tree":[{"path":"content/old.md","type":"blob","sha":"o1"},{"path":"README.md","type":"blob","sha":"r1"}]}
                """))
            .On("POST", R + "/git/blobs", Json("""{"sha":"nb1"}""", HttpStatusCode.Created))
            .On("POST", R + "/git/trees", Json("""{"sha":"nt1"}""", HttpStatusCode.Created))
            .On("POST", R + "/git/commits", Json("""{"sha":"nc1"}""", HttpStatusCode.Created))
            .On("PATCH", R + "/git/refs/heads/main", Json("""{"ref":"refs/heads/main","object":{"sha":"nc1"}}"""));
        using var pools = new IoPoolRegistry();

        var result = await Run(Client(pools, gh).Push(new GitHubPushRequest
        {
            RepositoryUrl = Repo,
            Branch = "main",
            Subdirectory = "content",
            Files = [new RepoFile("new.md", "# new")],
            CommitMessage = "sync",
            AuthorName = "Mesh",
            AuthorEmail = "mesh@example.com",
            AccessToken = "t",
        }));

        Assert.Equal("nc1", result.CommitSha);
        Assert.Equal(1, result.FilesDeleted);

        var blob = Body(gh, "POST", R + "/git/blobs");
        Assert.Equal("# new", blob.GetProperty("content").GetString());
        Assert.Equal("utf-8", blob.GetProperty("encoding").GetString());

        var tree = Body(gh, "POST", R + "/git/trees");
        Assert.False(tree.TryGetProperty("base_tree", out _));
        var items = tree.GetProperty("tree").EnumerateArray()
            .Select(i => (i.GetProperty("path").GetString(), i.GetProperty("sha").GetString(),
                i.GetProperty("mode").GetString(), i.GetProperty("type").GetString()))
            .OrderBy(i => i.Item1, StringComparer.Ordinal).ToArray();
        // README.md is outside the prefix and carried over by sha; content/old.md is mirrored away.
        Assert.Equal([("README.md", "r1", "100644", "blob"), ("content/new.md", "nb1", "100644", "blob")], items);

        var commit = Body(gh, "POST", R + "/git/commits");
        Assert.Equal("sync", commit.GetProperty("message").GetString());
        Assert.Equal("nt1", commit.GetProperty("tree").GetString());
        Assert.Equal(["p1"], commit.GetProperty("parents").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("Mesh", commit.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("mesh@example.com", commit.GetProperty("committer").GetProperty("email").GetString());
        Assert.True(DateTimeOffset.TryParse(commit.GetProperty("author").GetProperty("date").GetString(), out _));

        var move = Body(gh, "PATCH", R + "/git/refs/heads/main");
        Assert.Equal("nc1", move.GetProperty("sha").GetString());
        Assert.False(move.GetProperty("force").GetBoolean());
    }

    [Fact]
    public async Task APushToAnEmptyRepository_SeedsItThroughTheContentsApi_ThenMirrorsOverTheSeed()
    {
        var gh = new FakeGitHub()
            .On("GET", R, Json("""{"name":"widgets","owner":{"login":"acme"}}"""))
            // 409 "Git Repository is empty." first; after the seed the branch exists.
            .OnSequence("GET", R + "/git/ref/heads/main",
                Json("""{"message":"Git Repository is empty."}""", HttpStatusCode.Conflict),
                Json("""{"object":{"sha":"seed"}}"""))
            .On("GET", R + "/branches?per_page=1", Json("[]"))
            .On("PUT", R + "/contents/.gitkeep", Json("""{"content":{"sha":"x"}}""", HttpStatusCode.Created))
            .On("GET", R + "/git/commits/seed", Json("""{"sha":"seed","tree":{"sha":"ts"}}"""))
            .On("GET", R + "/git/trees/ts?recursive=1", Json("""{"tree":[{"path":".gitkeep","type":"blob","sha":"k"}]}"""))
            .On("POST", R + "/git/blobs", Json("""{"sha":"nb"}""", HttpStatusCode.Created))
            .On("POST", R + "/git/trees", Json("""{"sha":"nt"}""", HttpStatusCode.Created))
            .On("POST", R + "/git/commits", Json("""{"sha":"nc"}""", HttpStatusCode.Created))
            .On("PATCH", R + "/git/refs/heads/main", Json("""{"object":{"sha":"nc"}}"""));
        using var pools = new IoPoolRegistry();

        var result = await Run(Client(pools, gh).Push(new GitHubPushRequest
        {
            RepositoryUrl = Repo,
            Branch = "main",
            Files = [new RepoFile("a.md", "a")],
            CommitMessage = "first",
            AuthorName = "Mesh",
            AuthorEmail = "mesh@example.com",
            AccessToken = "t",
            CreateBranchIfMissing = true,
        }));

        Assert.Equal("nc", result.CommitSha);
        var seed = Body(gh, "PUT", R + "/contents/.gitkeep");
        Assert.Equal("main", seed.GetProperty("branch").GetString());
        Assert.Equal("Created by MeshWeaver to initialize this repository.\n",
            Encoding.UTF8.GetString(Convert.FromBase64String(seed.GetProperty("content").GetString()!)));
        // A whole-repo mirror drops the seed: the tree is the export alone.
        var tree = Body(gh, "POST", R + "/git/trees").GetProperty("tree");
        Assert.Equal(["a.md"], tree.EnumerateArray().Select(i => i.GetProperty("path").GetString()));
    }

    [Theory]
    [InlineData("acme", "user/repos")]
    [InlineData("someone-else", "orgs/acme/repos")]
    public async Task AMissingRepositoryIsCreatedPrivate_UnderTheUserOrTheOrganisation(string login, string createPath)
    {
        var gh = new FakeGitHub()
            .On("GET", R, Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound))
            .On("GET", "/user", Json($$"""{"login":"{{login}}"}"""))
            .On("POST", "/" + createPath, Json("""{"name":"widgets","owner":{"login":"acme"}}""", HttpStatusCode.Created));
        using var pools = new IoPoolRegistry();

        Assert.True(await Run(Client(pools, gh).EnsureRepoExists(Repo, "t", createIfMissing: true)));

        var body = Body(gh, "POST", "/" + createPath);
        Assert.Equal("widgets", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("private").GetBoolean());
        Assert.False(body.GetProperty("auto_init").GetBoolean());
    }

    // ── compare ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangedPaths_AreTheComparedFilesUnderThePrefix_RenamesCountingBothNames()
    {
        var gh = new FakeGitHub().On("GET", R + "/compare/b1...feat%2Fx", Json("""
            {"status":"ahead","files":[
              {"filename":"content/new.md","previous_filename":"content/old.md"},
              {"filename":"elsewhere/x.md"}]}
            """));
        using var pools = new IoPoolRegistry();

        var changed = await Run(Client(pools, gh).GetChangedPaths(Repo, "b1", "feat/x", "content", "t"));

        Assert.Equal(["new.md", "old.md"], changed);
    }

    [Theory]
    [InlineData("""{"status":"diverged","files":[]}""")]
    [InlineData("""{"status":"behind","files":[]}""")]
    [InlineData("""{"status":"ahead"}""")]
    public async Task ChangedPaths_AreUnknownWhenTheDiffCannotBeTrusted(string answer)
    {
        var gh = new FakeGitHub().On("GET", R + "/compare/b1...h1", Json(answer));
        using var pools = new IoPoolRegistry();

        Assert.Null(await Run(Client(pools, gh).GetChangedPaths(Repo, "b1", "h1", null, "t")));
    }

    [Fact]
    public async Task ChangedPaths_AtTheFileCap_OrOnAFailure_FallBackToAFullImport()
    {
        var files = string.Join(",", Enumerable.Range(0, 300).Select(i => $$"""{"filename":"f{{i}}"}"""));
        var gh = new FakeGitHub()
            .On("GET", R + "/compare/b1...h1", Json($$"""{"status":"ahead","files":[{{files}}]}"""))
            .On("GET", R + "/compare/b1...h2", Json("""{"message":"Server Error"}""", HttpStatusCode.InternalServerError));
        using var pools = new IoPoolRegistry();
        var client = Client(pools, gh);

        Assert.Null(await Run(client.GetChangedPaths(Repo, "b1", "h1", null, "t")));
        Assert.Null(await Run(client.GetChangedPaths(Repo, "b1", "h2", null, "t")));
    }

    [Theory]
    [InlineData("ahead", true)]
    [InlineData("identical", true)]
    [InlineData("behind", false)]
    [InlineData("diverged", false)]
    public async Task IsAncestor_ReadsTheCompareStatus(string status, bool expected)
    {
        var gh = new FakeGitHub().On("GET", R + "/compare/a1...main", Json($$"""{"status":"{{status}}"}"""));
        using var pools = new IoPoolRegistry();

        Assert.Equal(expected, await Run(Client(pools, gh).IsAncestor(Repo, "a1", "main", "t")));
    }

    [Fact]
    public async Task IsAncestor_IsFalseWhenTheCompareFails()
    {
        var gh = new FakeGitHub().On("GET", R + "/compare/a1...main", Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        using var pools = new IoPoolRegistry();

        Assert.False(await Run(Client(pools, gh).IsAncestor(Repo, "a1", "main", "t")));
    }

    // ── branches & pull requests ─────────────────────────────────────────────

    [Fact]
    public async Task CreateBranch_ResolvesTheBaseAndPostsTheNewRef()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/git/ref/heads/main", Json("""{"object":{"sha":"base1"}}"""))
            .On("POST", R + "/git/refs", Json("""{"ref":"refs/heads/sync/1","object":{"sha":"base1"}}""", HttpStatusCode.Created));
        using var pools = new IoPoolRegistry();

        var result = await Run(Client(pools, gh).CreateBranch(new GitHubCreateBranchRequest
        {
            RepositoryUrl = Repo, NewBranch = "sync/1", BaseRef = "main", AccessToken = "t",
        }));

        Assert.Equal(new GitHubBranchResult("sync/1", "base1"), result);
        var body = Body(gh, "POST", R + "/git/refs");
        Assert.Equal("refs/heads/sync/1", body.GetProperty("ref").GetString());
        Assert.Equal("base1", body.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task OpenPullRequest_PostsTitleHeadBaseBody()
    {
        var gh = new FakeGitHub().On("POST", R + "/pulls",
            Json("""{"number":7,"html_url":"https://github.com/acme/widgets/pull/7","state":"open"}""", HttpStatusCode.Created));
        using var pools = new IoPoolRegistry();

        var info = await Run(Client(pools, gh).OpenPullRequest(new GitHubOpenPullRequestRequest
        {
            RepositoryUrl = Repo, Title = "Sync", Body = null, HeadBranch = "sync/1", BaseBranch = "main", AccessToken = "t",
        }));

        Assert.Equal(new GitHubPullRequestInfo(7, "https://github.com/acme/widgets/pull/7", PullRequestStatus.Open), info);
        var body = Body(gh, "POST", R + "/pulls");
        Assert.Equal("Sync", body.GetProperty("title").GetString());
        Assert.Equal("sync/1", body.GetProperty("head").GetString());
        Assert.Equal("main", body.GetProperty("base").GetString());
        Assert.Equal("", body.GetProperty("body").GetString());
    }

    [Fact]
    public async Task APullRequestListing_FollowsEveryPage_AndReadsMergedOffMergedAt()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/pulls?state=closed&sort=created&direction=desc&per_page=100",
                Json("""[{"number":1,"title":"a","state":"closed","merged_at":"2026-10-01T00:00:00Z","html_url":"u1","head":{"ref":"h"},"base":{"ref":"main"}}]""",
                    link: "<" + Api + "repos/acme/widgets/pulls?state=closed&page=2>; rel=\"next\", <" + Api + "x>; rel=\"last\""))
            .On("GET", R + "/pulls?state=closed&page=2",
                Json("""[{"number":2,"title":"b","state":"closed","merged_at":null,"html_url":"u2","draft":true}]"""));
        using var pools = new IoPoolRegistry();

        var list = await Run(Client(pools, gh).ListPullRequests(Repo, PullRequestStatus.Merged, "t"));

        Assert.Equal([(1, PullRequestStatus.Merged, false), (2, PullRequestStatus.Closed, true)],
            list.Select(p => (p.Number, p.Status, p.Draft)));
        Assert.Equal("h", list[0].HeadBranch);
        Assert.Equal(2, gh.Requests.Count);
    }

    [Fact]
    public async Task PullRequestDetail_RollsUpLatestReviewsAndPagedCheckRuns()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/pulls/9", Json("""
                {"number":9,"title":"t","body":"b","state":"open","merged":false,"head":{"ref":"h","sha":"hs"},"base":{"ref":"main"},
                 "html_url":"u","mergeable":true,"mergeable_state":"clean","comments":4,"user":{"login":"dev"}}
                """))
            .On("GET", R + "/pulls/9/reviews?per_page=100", Json("""
                [{"user":{"login":"r1"},"state":"CHANGES_REQUESTED","submitted_at":"2026-10-01T00:00:00Z"},
                 {"user":{"login":"r1"},"state":"APPROVED","submitted_at":"2026-10-02T00:00:00Z"},
                 {"user":{"login":"r2"},"state":"COMMENTED","submitted_at":"2026-10-01T00:00:00Z"}]
                """))
            .On("GET", R + "/commits/hs/check-runs?per_page=100",
                Json("""{"total_count":3,"check_runs":[{"status":"completed","conclusion":"success"},{"status":"in_progress"}]}""",
                    link: "<" + Api + "repos/acme/widgets/commits/hs/check-runs?page=2>; rel=\"next\""))
            .On("GET", R + "/commits/hs/check-runs?page=2",
                Json("""{"total_count":3,"check_runs":[{"status":"completed","conclusion":"failure"}]}"""));
        using var pools = new IoPoolRegistry();

        var detail = await Run(Client(pools, gh).GetPullRequestDetail(Repo, 9, "t"));

        Assert.Equal(new GitHubReviewSummary(Approved: 1, ChangesRequested: 0, Commented: 1), detail.Reviews);
        Assert.Equal(new GitHubCheckSummary(3, 1, 1, 1, GitHubCheckState.Failed), detail.Checks);
        Assert.Equal("clean", detail.MergeableState);
        Assert.Equal(4, detail.CommentsCount);
        Assert.Equal("hs", detail.HeadSha);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task PullRequestDetail_DegradesOnlyMissingOrForbiddenChecksToEmpty(HttpStatusCode status, bool degrades)
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/pulls/9", Json("""{"number":9,"title":"t","state":"open","head":{"sha":"hs"},"html_url":"u"}"""))
            .On("GET", R + "/pulls/9/reviews?per_page=100", Json("[]"))
            .On("GET", R + "/commits/hs/check-runs?per_page=100", Json("""{"message":"nope"}""", status));
        using var pools = new IoPoolRegistry();
        var detail = Client(pools, gh).GetPullRequestDetail(Repo, 9, "t");

        if (degrades)
            Assert.Equal(GitHubCheckSummary.Empty, (await Run(detail)).Checks);
        else
            Assert.Equal(status, (await Assert.ThrowsAsync<GitHubApiException>(() => Run(detail))).StatusCode);
    }

    [Fact]
    public async Task Merge_PutsTheMethod_AndOmitsAnUnsetTitle()
    {
        var gh = new FakeGitHub().On("PUT", R + "/pulls/9/merge", Json("""{"merged":true,"sha":"m1","message":"Pull Request successfully merged"}"""));
        using var pools = new IoPoolRegistry();

        var result = await Run(Client(pools, gh).MergePullRequest(new GitHubMergePullRequestRequest
        {
            RepositoryUrl = Repo, Number = 9, Method = GitHubMergeMethod.Squash, CommitMessage = "msg", AccessToken = "t",
        }));

        Assert.Equal(new GitHubMergeResult(true, "m1", "Pull Request successfully merged"), result);
        var body = Body(gh, "PUT", R + "/pulls/9/merge");
        Assert.Equal("squash", body.GetProperty("merge_method").GetString());
        Assert.Equal("msg", body.GetProperty("commit_message").GetString());
        Assert.False(body.TryGetProperty("commit_title", out _));
    }

    // ── issues ───────────────────────────────────────────────────────────────

    private const string IssueJson = """
        {"number":5,"title":"Bug","body":"b","state":"closed","state_reason":"duplicate","user":{"login":"dev"},
         "labels":[{"name":"bug"}],"assignees":[{"login":"a1"}],"comments":1,"html_url":"https://github.com/acme/widgets/issues/5",
         "created_at":"2026-10-01T00:00:00Z","updated_at":"2026-10-02T00:00:00Z","closed_at":"2026-10-03T00:00:00Z"}
        """;

    [Fact]
    public async Task AnIssueListing_DropsPullRequests_AndFollowsPages()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/issues?state=all&sort=created&direction=desc&per_page=100",
                Json("[" + IssueJson + """,{"number":6,"state":"open","pull_request":{"url":"x"}}]""",
                    link: "<" + Api + "repos/acme/widgets/issues?page=2>; rel=\"next\""))
            .On("GET", R + "/issues?page=2", Json("""[{"number":8,"state":"open"}]"""));
        using var pools = new IoPoolRegistry();

        var issues = await Run(Client(pools, gh).ListIssues(Repo, null, "t"));

        Assert.Equal([5, 8], issues.Select(i => i.Number));
        var first = issues[0];
        Assert.Equal(GitHubIssueState.Closed, first.State);
        Assert.Equal(GitHubIssueStateReason.Duplicate, first.StateReason);
        Assert.Equal(["bug"], first.Labels);
        Assert.Equal(["a1"], first.Assignees);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00Z"), first.ClosedAt);
    }

    [Fact]
    public async Task GetIssue_ReadsEveryComment_AndEarnsCommentsAreComplete()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/issues/5", Json(IssueJson))
            .On("GET", R + "/issues/5/comments?per_page=100", Json("""
                [{"id":9000000001,"user":{"login":"c"},"body":"hi","created_at":"2026-10-01T00:00:00Z","html_url":"cu"}]
                """));
        using var pools = new IoPoolRegistry();

        var issue = await Run(Client(pools, gh).GetIssue(Repo, 5, "t"));

        Assert.True(issue.CommentsAreComplete);
        Assert.Equal(new GitHubIssueComment(9000000001, "c", "hi", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "cu"),
            Assert.Single(issue.Comments));
    }

    [Fact]
    public async Task GetIssue_KeepsATransferredIssueWhoseCommentsLeg404s_DeclaringTheGap()
    {
        var gh = new FakeGitHub()
            .On("GET", R + "/issues/5", Json(IssueJson))
            .On("GET", R + "/issues/5/comments?per_page=100", Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        using var pools = new IoPoolRegistry();

        var issue = await Run(Client(pools, gh).GetIssue(Repo, 5, "t"));

        Assert.Equal(5, issue.Number);
        Assert.False(issue.CommentsAreComplete);
    }

    [Fact]
    public async Task AMissingIssue_FaultsGetIssue_AndIsNullForFindIssueState()
    {
        var gh = new FakeGitHub().On("GET", R + "/issues/404", Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound));
        using var pools = new IoPoolRegistry();
        var client = Client(pools, gh);

        var fault = await Assert.ThrowsAsync<GitHubNotFoundException>(() => Run(client.GetIssue(Repo, 404, "t")));
        Assert.Equal("Not Found", fault.Message);
        Assert.Null(await Run(client.FindIssueState(Repo, 404, "t")));
    }

    [Fact]
    public async Task CreateCommentAndClose_SendTheDocumentedBodies()
    {
        var gh = new FakeGitHub()
            .On("POST", R + "/issues", Json(IssueJson, HttpStatusCode.Created))
            .On("POST", R + "/issues/5/comments", Json("""{"id":1,"body":"hello"}""", HttpStatusCode.Created))
            .On("PATCH", R + "/issues/5", Json(IssueJson));
        using var pools = new IoPoolRegistry();
        var client = Client(pools, gh);

        await Run(client.CreateIssue(new GitHubCreateIssueRequest
        {
            RepositoryUrl = Repo, Title = "Bug", Body = null, Labels = ["bug", "sev:L"], AccessToken = "t",
        }));
        await Run(client.CommentIssue(Repo, 5, "hello", "t"));
        await Run(client.SetIssueState(Repo, 5, GitHubIssueState.Closed, "t"));

        var created = Body(gh, "POST", R + "/issues");
        Assert.Equal("Bug", created.GetProperty("title").GetString());
        Assert.Equal("", created.GetProperty("body").GetString());
        Assert.Equal(["bug", "sev:L"], created.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("hello", Body(gh, "POST", R + "/issues/5/comments").GetProperty("body").GetString());
        Assert.Equal("closed", Body(gh, "PATCH", R + "/issues/5").GetProperty("state").GetString());
    }

    // ── error mapping ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(401, """{"message":"Bad credentials"}""", null, typeof(GitHubAuthorizationException), "Bad credentials")]
    [InlineData(403, """{"message":"Resource not accessible by integration"}""", null, typeof(GitHubForbiddenException), "Resource not accessible by integration")]
    [InlineData(403, """{"message":"API rate limit exceeded for installation"}""", "0", typeof(GitHubRateLimitExceededException), "API rate limit exceeded for installation")]
    [InlineData(403, """{"message":"You have exceeded a secondary rate limit"}""", "12", typeof(GitHubRateLimitExceededException), "You have exceeded a secondary rate limit")]
    [InlineData(404, """{"message":"Not Found"}""", null, typeof(GitHubNotFoundException), "Not Found")]
    [InlineData(409, """{"message":"Git Repository is empty."}""", null, typeof(GitHubApiException), "Git Repository is empty.")]
    [InlineData(422, """{"message":"Validation Failed","errors":[{"code":"already_exists"}]}""", null, typeof(GitHubValidationException), "Validation Failed")]
    [InlineData(429, """{"message":"slow down"}""", null, typeof(GitHubRateLimitExceededException), "slow down")]
    [InlineData(502, "<html>bad gateway</html>", null, typeof(GitHubApiException), "GitHub answered HTTP 502 (BadGateway).")]
    public void AFailureMapsToItsTypedException_CarryingGitHubsOwnMessage(
        int status, string body, string? remaining, Type expected, string message)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        if (remaining is not null)
            response.Headers.Add("x-ratelimit-remaining", remaining);

        var failure = GitHubRestApi.Failure((HttpStatusCode)status, body, response.Headers);

        Assert.IsType(expected, failure);
        Assert.Equal((HttpStatusCode)status, failure.StatusCode);
        Assert.Equal(message, failure.Message);
        // An ANSWER, never a transport fault: generic retry classifiers key off HttpRequestException.
        Assert.IsNotAssignableFrom<HttpRequestException>(failure);
        if (failure is GitHubValidationException validation)
            Assert.Contains("already_exists", validation.Errors);
    }

    [Fact]
    public void AnExhaustedBudgetCarriesItsResetTime()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        response.Headers.Add("x-ratelimit-reset", "1790000000");

        var failure = Assert.IsType<GitHubRateLimitExceededException>(
            GitHubRestApi.Failure(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", response.Headers));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), failure.Reset);
    }

    /// <summary>The empty-repo tolerance the push path builds on: a missing branch (404) and an unborn
    /// HEAD (409 "Git Repository is empty.") both mean "no commit to build on"; nothing else does.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Conflict, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void MissingOrEmptyRepo_IsExactly404Or409(HttpStatusCode status, bool expected)
        => Assert.Equal(expected, OctokitGitHubRepoClient.IsMissingOrEmptyRepo(new GitHubApiException(status, "x")));

    [Fact]
    public void TheLinkHeaderNextPageIsParsed_AndTheLastPageHasNone()
    {
        Assert.Equal("https://api.github.com/x?page=2",
            GitHubRestApi.NextPage("<https://api.github.com/x?page=2>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\""));
        Assert.Null(GitHubRestApi.NextPage("<https://api.github.com/x?page=1>; rel=\"prev\""));
        Assert.Null(GitHubRestApi.NextPage(null));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static OctokitGitHubRepoClient Client(IoPoolRegistry pools, FakeGitHub gh)
        => new(pools, logger: null, httpClient: new HttpClient(gh));

    private static Task<T> Run<T>(IObservable<T> source)
        => source.FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    private static JsonElement Body(FakeGitHub gh, string method, string path)
    {
        var request = Assert.Single(gh.Requests, r => r.Method == method && r.Path == path);
        Assert.Equal("application/json", request.ContentType);
        return JsonDocument.Parse(request.Body!).RootElement;
    }

    private static Answer Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? link = null)
        => new(status, body, null, link);

    private static Answer Redirect(HttpStatusCode status, string location)
        => new(status, "", location, null);

    private sealed record Answer(HttpStatusCode Status, string Body, string? Location, string? Link);

    private sealed record Seen(
        string Method, string Path, string? Authorization, string? UserAgent, string? Accept,
        string? ApiVersion, string? ContentType, string? Body)
    {
        public string Line => Method + " " + Path;
    }

    /// <summary>
    /// Routes "METHOD path?query" (path relative to api.github.com, or an absolute URL for another
    /// host) to recorded answers, and records every request with the headers and body it carried.
    /// A sequence route answers its entries in order, then repeats the last.
    /// </summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private ImmutableDictionary<string, ImmutableQueue<Answer>> routes =
            ImmutableDictionary<string, ImmutableQueue<Answer>>.Empty;
        private ImmutableList<Seen> requests = ImmutableList<Seen>.Empty;

        public IReadOnlyList<Seen> Requests => Volatile.Read(ref requests);

        public FakeGitHub On(string method, string path, Answer answer) => OnSequence(method, path, answer);

        public FakeGitHub OnSequence(string method, string path, params Answer[] answers)
        {
            routes = routes.SetItem(method + " " + path, ImmutableQueue.CreateRange(answers));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.Host == "api.github.com" ? uri.PathAndQuery : uri.GetLeftPart(UriPartial.Query);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ImmutableInterlocked.Update(ref requests, list => list.Add(new Seen(
                request.Method.Method, path,
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var v) ? v.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                body)));

            var key = request.Method.Method + " " + path;
            Answer? answer = null;
            ImmutableInterlocked.Update(ref routes, map =>
            {
                if (!map.TryGetValue(key, out var queue))
                    return map;
                answer = queue.Peek();
                var rest = queue.Dequeue();
                return rest.IsEmpty ? map : map.SetItem(key, rest);
            });
            if (answer is null)
                return new HttpResponseMessage((HttpStatusCode)418)
                {
                    Content = new StringContent($$"""{"message":"unrouted {{key}}"}"""),
                    RequestMessage = request,
                };

            var response = new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            if (answer.Location is not null)
                response.Headers.Location = new Uri(answer.Location);
            if (answer.Link is not null)
                response.Headers.TryAddWithoutValidation("Link", answer.Link);
            return response;
        }
    }
}
