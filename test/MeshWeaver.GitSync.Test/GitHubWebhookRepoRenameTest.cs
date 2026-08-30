using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins #1856 — <b>a renamed repository silently stops syncing every Space, and the zero-match
/// whispers.</b>
///
/// <para><c>Systemorph/education</c> was renamed to <c>Systemorph/MeshWeaver.Education</c>. GitHub
/// redirects the old url, so git, <c>gh</c>, the REST API and every manual sync kept working —
/// nothing looked broken anywhere. But a webhook payload always carries the repository's CURRENT
/// name, and the stored config still held the old one: <c>education</c> vs
/// <c>MeshWeaver.Education</c> is not a casing difference any comparer can bridge. Ten course Spaces
/// stopped importing and served content 65 commits stale for four days, while every delivery
/// reported success.</para>
///
/// <para>Two halves are pinned here, and the second is the one that cost the four days:</para>
/// <list type="number">
///   <item>matching survives a rename — the CANONICAL identity is consulted when the stored strings
///     match nothing, and the config is repointed so the drift is repaired rather than tolerated;</item>
///   <item>a delivery that matches NOTHING is reported at <b>Warning</b>, naming the incoming
///     repository AND everything it was compared against. At Information it is indistinguishable
///     from the healthy "nothing to do" line beside it.</item>
/// </list>
/// </summary>
public class GitHubWebhookRepoRenameTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private readonly WebhookLogCapture capture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s =>
            {
                s.AddSingleton<ILoggerProvider>(capture);
                return s;
            });

    /// <summary>
    /// 🚨 THE defect. A config storing the repository's OLD name must still match a green build that
    /// arrives under the NEW one — and the import must actually run, which is the only proof that
    /// matters (a matched-but-not-imported Space is still a stale Space).
    ///
    /// <para>The config is then REPOINTED to the current url, so the repair is permanent: the next
    /// delivery matches on the free string path, no lookup is needed, and the Space's GitHub settings
    /// stop displaying a name the repository no longer has.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task GreenBuild_UnderTheNewName_MatchesAConfigStoringTheOldOne_AndRepointsIt()
    {
        await Connect();
        const string oldUrl = "https://github.com/test/course-old";
        const string newUrl = "https://github.com/Systemorph/Course-Renamed";

        // The rename has ALREADY happened: GitHub redirects the old url for every operation, so both
        // names address one repository. Only equality against the stored string is broken.
        Fake.Rename(oldUrl, newUrl);

        // Space A authors the content and commits it through the OLD url — as every existing config does.
        var a = "GhRa" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Rename Source Space");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nContent from the renamed repo.");
        await Sync.SaveConfig(a, oldUrl, "main", null, true, true).Timeout(30.Seconds()).Await();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).Await();

        // Space B syncs the same repository by its OLD name and has never imported.
        var b = "GhRb" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(b, "Rename Target Space");
        await Sync.SaveConfig(b, oldUrl, "main", null, true, true).Timeout(30.Seconds()).Await();

        // The webhook request is ANONYMOUS (its authorization is the verified HMAC) — drop every
        // ambient identity so the processor's own System impersonation is what carries the reads.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearHostIdentity();
        accessService.SetHostIdentity(null);
        accessService.SetContext(null);
        int recorded;
        try
        {
            // Nothing has imported yet — without this the assertion below could pass on content that
            // was never moved by this delivery.
            Assert.Null(await ReadNode($"{b}/Welcome").Timeout(30.Seconds()).Await());

            // The payload carries the repository's CURRENT name. Against the string-only matcher this
            // matches NO config and imports nothing.
            recorded = await Webhooks.Process("workflow_run", GreenBuild("Systemorph/Course-Renamed"))
                .Timeout(60.Seconds()).Await();
        }
        finally
        {
            accessService.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }

        Assert.Equal(1, recorded);

        // The import ran: the content reached the Space whose config still said "course-old".
        var imported = await WaitForNode($"{b}/Welcome");
        Assert.Contains("Content from the renamed repo.", MarkdownBody(imported));

        // …and the stale url was repaired in place, on BOTH configs that carried it.
        var repointed = await WaitForConfig(b, c => c.RepositoryUrl == newUrl);
        Assert.Equal(newUrl, repointed.RepositoryUrl);
        await WaitForConfig(a, c => c.RepositoryUrl == newUrl);

        // The repair is announced, not silent — a config changing under a mesh must be readable in
        // the log that changed it.
        Assert.Contains(capture.Entries, e =>
            e.Level >= LogLevel.Warning
            && e.Message.Contains("RENAMED")
            && e.Message.Contains("test/course-old"));
    }

    /// <summary>
    /// 🚨 The half that cost four days. A delivery matching NO sync config means every Space that
    /// syncs that repository has just been skipped — a stale config, a rename, or a hook on the wrong
    /// repository, each of which wants a human. It must be <b>Warning</b> and it must name BOTH
    /// sides: at Information, beside the healthy "matched no sync source that needs updating" line,
    /// it is indistinguishable from a mesh with nothing to do.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task NoConfigTargetsTheRepository_IsReportedAtWarning_NamingBothSides()
    {
        await Connect();
        var space = "GhRu" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Unmatched Space");
        await Sync.SaveConfig(space, "https://github.com/test/only-this-repo", "main", null, true, true)
            .Timeout(30.Seconds()).Await();

        var payload = JsonDocument.Parse("""
        {
          "action": "opened",
          "repository": { "full_name": "someone/never-configured" },
          "issue": { "number": 1, "title": "nope", "state": "open", "comments": 0 }
        }
        """).RootElement;

        Assert.Equal(0, await Webhooks.Process("issues", payload).Timeout(60.Seconds()).Await());

        var zeroMatch = await WaitForRecord(e =>
            e.Level >= LogLevel.Warning && e.Message.Contains("someone/never-configured"));

        // Both sides, in one line: what arrived, and what it was compared against. A report naming
        // only the incoming repository sends the reader hunting for the config list by hand.
        Assert.Contains("someone/never-configured", zeroMatch.Message);
        Assert.Contains("test/only-this-repo", zeroMatch.Message);
        Assert.Contains("RENAMED", zeroMatch.Message);
        // …and the denominator, so "matched nothing" cannot be misread as "there was nothing to match".
        Assert.Matches(@"NONE of the \d+ sync config", zeroMatch.Message);
    }

    /// <summary>
    /// Regression guard: the case-insensitive match that ALREADY worked must keep working — GitHub
    /// treats owner and repository names case-insensitively, and <c>education</c> vs
    /// <c>Education</c> was never the problem.
    ///
    /// <para>It must also stay FREE: a stored name that already matches costs no canonical lookup, so
    /// the rename tolerance can never become a per-delivery network call on the hot path.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task CaseOnlyDifference_StillMatches_WithoutAskingGitHubAnything()
    {
        await Connect();
        var space = "GhRc" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Case Space");
        await Sync.SaveConfig(space, "https://github.com/Test/Case-Repo", "main", null, true, true)
            .Timeout(30.Seconds()).Await();

        var before = Fake.CanonicalLookups;
        var payload = JsonDocument.Parse("""
        {
          "action": "opened",
          "repository": { "full_name": "test/case-repo" },
          "issue": {
            "number": 11, "title": "Case-insensitive", "state": "open",
            "user": { "login": "octocat" }, "labels": [], "assignees": [],
            "comments": 0, "html_url": "https://github.com/test/case-repo/issues/11"
          }
        }
        """).RootElement;

        Assert.Equal(1, await Webhooks.Process("issues", payload).Timeout(60.Seconds()).Await());
        var issue = await WaitForIssue(IssueService.IssuePath(space, 11), i => i.Number == 11);
        Assert.Equal("Case-insensitive", issue.Title);

        // The stored string matched, so GitHub was never asked.
        Assert.Equal(before, Fake.CanonicalLookups);
    }

    /// <summary>
    /// 🚨 The cache must not be able to make a stale name stick forever. A canonical name is a fact
    /// with a shelf life — a repository can be renamed AGAIN, and a resolution that failed under a
    /// weak identity must get another turn — so every entry is re-resolved once it is older than
    /// <see cref="GitHubRepoIdentityResolver.Ttl"/>, and a cached answer is served for free until then.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task ResolvedIdentity_IsCached_ButExpires_SoAStaleNameCannotStick()
    {
        const string url = "https://github.com/test/renamed-twice";
        Fake.Rename(url, "https://github.com/test/second-name");

        var clock = new TestClock(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var resolver = new GitHubRepoIdentityResolver(Fake, Credentials, appTokens: null, timeProvider: clock);

        var before = Fake.CanonicalLookups;
        Assert.Equal("test/second-name",
            (await resolver.Resolve(url, UserId).Timeout(30.Seconds()).Await())?.ToString());
        // A second ask inside the TTL is served from the cache — this is what keeps the fallback off
        // the per-delivery path.
        Assert.Equal("test/second-name",
            (await resolver.Resolve(url, UserId).Timeout(30.Seconds()).Await())?.ToString());
        Assert.Equal(before + 1, Fake.CanonicalLookups);

        // The repository is renamed AGAIN. Inside the TTL the cache still answers with the old fact —
        // correct, and bounded.
        Fake.Rename(url, "https://github.com/test/third-name");
        clock.Advance(GitHubRepoIdentityResolver.Ttl - TimeSpan.FromMinutes(1));
        Assert.Equal("test/second-name",
            (await resolver.Resolve(url, UserId).Timeout(30.Seconds()).Await())?.ToString());
        Assert.Equal(before + 1, Fake.CanonicalLookups);

        // Past the TTL it is re-resolved — the entry expires rather than latching.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("test/third-name",
            (await resolver.Resolve(url, UserId).Timeout(30.Seconds()).Await())?.ToString());
        Assert.Equal(before + 2, Fake.CanonicalLookups);
    }

    /// <summary>
    /// Repointing keeps the stored url's scheme and HOST: a GitHub Enterprise config must never be
    /// silently moved to github.com by a repair. Only the owner/repo path is rewritten.
    /// </summary>
    [Fact]
    public void RepointUrl_RewritesOwnerAndRepo_ButNeverTheHost()
    {
        var canonical = new RepoIdentity("Systemorph", "MeshWeaver.Education");

        Assert.Equal("https://github.com/Systemorph/MeshWeaver.Education",
            GitHubWebhookProcessor.RepointUrl("https://github.com/Systemorph/education", canonical));
        Assert.Equal("https://ghe.corp.example/Systemorph/MeshWeaver.Education",
            GitHubWebhookProcessor.RepointUrl("https://ghe.corp.example/Systemorph/education", canonical));
        // A `.git` suffix and a trailing slash are both normalised away by the rewrite.
        Assert.Equal("https://github.com/Systemorph/MeshWeaver.Education",
            GitHubWebhookProcessor.RepointUrl("https://github.com/Systemorph/education.git", canonical));
        // The owner/repo shorthand is not an absolute uri — fall back to the canonical github.com url.
        Assert.Equal("https://github.com/Systemorph/MeshWeaver.Education",
            GitHubWebhookProcessor.RepointUrl("Systemorph/education", canonical));
    }

    /// <summary>
    /// Identity equality is the whole defect in one method: case-insensitive (GitHub is), never true
    /// for a half-parsed identity, and — the part no comparer can fix — <c>education</c> and
    /// <c>MeshWeaver.Education</c> are DIFFERENT, which is why the canonical lookup has to exist.
    /// </summary>
    [Fact]
    public void RepoIdentity_MatchesCaseInsensitively_ButARenameIsNotACasingDifference()
    {
        var incoming = new RepoIdentity("Systemorph", "MeshWeaver.Education");

        Assert.True(incoming.Matches(new RepoIdentity("systemorph", "meshweaver.education")));
        Assert.False(incoming.Matches(new RepoIdentity("Systemorph", "education")));
        Assert.False(incoming.Matches(new RepoIdentity("Systemorph", "Education")));
        Assert.False(incoming.Matches(new RepoIdentity("Someone", "MeshWeaver.Education")));
        Assert.False(incoming.Matches(null));
        Assert.False(incoming.Matches(new RepoIdentity("", "MeshWeaver.Education")));

        // Parsing tolerates the shapes a config can hold, and refuses the ones it cannot.
        Assert.Equal("Systemorph/education",
            GitHubRepoIdentityResolver.Parse("https://github.com/Systemorph/education.git")?.ToString());
        Assert.Equal("Systemorph/education",
            GitHubRepoIdentityResolver.Parse("Systemorph/education")?.ToString());
        Assert.Null(GitHubRepoIdentityResolver.Parse(""));
        Assert.Null(GitHubRepoIdentityResolver.Parse(null));
        Assert.Null(GitHubRepoIdentityResolver.Parse("https://github.com/only-one-segment"));
    }

    private static JsonElement GreenBuild(string fullName)
        => JsonDocument.Parse($$"""
        { "action": "completed",
          "repository": { "full_name": "{{fullName}}", "default_branch": "main" },
          "workflow_run": { "conclusion": "success", "head_branch": "main",
                            "head_sha": "feedface", "id": 9, "run_number": 9,
                            "name": "CI", "event": "push", "updated_at": "2026-08-18T10:00:00Z" } }
        """).RootElement;

    /// <summary>Polls the captured processor log until a record matches — the capture is not an
    /// observable source, so the sanctioned interval re-query stands in for a stream wait.</summary>
    private async Task<WebhookLogCapture.Entry> WaitForRecord(Func<WebhookLogCapture.Entry, bool> predicate) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .Select(_ => capture.Entries.FirstOrDefault(predicate))
            .Where(e => e is not null)
            .Select(e => e!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await();

    /// <summary>A clock the test moves by hand, so TTL expiry is asserted rather than waited for.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Captures every record <see cref="GitHubWebhookProcessor"/> logs (instance-scoped —
    /// one per test, dies with the mesh).</summary>
    private sealed class WebhookLogCapture : ILoggerProvider
    {
        public sealed record Entry(LogLevel Level, string Message);

        private readonly ConcurrentQueue<Entry> entries = new();

        public IReadOnlyCollection<Entry> Entries => entries;

        public ILogger CreateLogger(string categoryName)
            => categoryName == typeof(GitHubWebhookProcessor).FullName
                ? new Sink(entries)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue(new Entry(logLevel, formatter(state, exception)));
        }
    }
}
