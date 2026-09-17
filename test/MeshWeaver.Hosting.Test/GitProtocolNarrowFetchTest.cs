using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The FILTERED fetch transfers only the blobs the filter keeps — the contract
/// <see cref="IGitHubRepoClient.Fetch(string,string,string?,string,Func{string,bool})"/> has always
/// stated (<i>"without pulling the rest of the repo"</i>) and that
/// <see cref="GitProtocolRepoClient"/> did not honour until #4222.
///
/// <para>It used to fetch the whole pack and filter while reading the worktree, on the reasoning
/// that the git protocol costs the same number of REST calls (zero) either way. True of CALLS,
/// false of BYTES. Measured against <c>MeshWeaver.Plugins</c> on 2026-09-16: the whole-content
/// transfer is 47.8 MB / 13 s, the 143 manifest files the plugin catalog's listing actually parses
/// are 0.8 MB, and the narrow path serves the identical answer at the identical commit in
/// 3.3 s / 1.3 MB. That 13 s was the dominant term in <c>GET /api/plugins</c>'s 12–19 s TTFB.</para>
///
/// <para>The discriminating assertion is the SELECTION line: a narrow fetch that silently
/// regressed to a whole checkout still returns the right files, so asserting the files alone
/// would pass over the defect this exists to prevent. Each test therefore pins WHICH path ran as
/// well as what it answered.</para>
/// </summary>
public class GitProtocolNarrowFetchTest
{
    /// <summary>The predicate the node-repo listing applies: each plugin folder's root and its
    /// manifest sidecar, and nothing else.</summary>
    private static bool IsListingInput(string path)
    {
        var slash = path.IndexOf('/');
        if (slash <= 0 || path.IndexOf('/', slash + 1) >= 0)
            return false;
        var name = path[(slash + 1)..];
        return name is "index.json" or "manifest.lock";
    }

    [Fact(Timeout = 120_000)]
    public async Task AFilteredFetch_TransfersOnlyTheMatchingBlobs()
    {
        using var world = await GitWorld.Create(partialCloneCapable: true);

        var snapshot = await world.Client
            .Fetch(world.OriginUrl, "main", null, "", IsListingInput)
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["A/index.json", "A/manifest.lock", "B/index.json"],
            snapshot.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(world.HeadSha, snapshot.CommitSha);

        // WHICH path ran: 3 of the repo's 5 paths were selected BEFORE any blob moved...
        Assert.Contains(world.Log, line => line.Contains("3 of 5 path(s) selected"));
        // ...and the sparse-checkout actually RESTRICTED the worktree. This line is emitted only
        // after `sparse-checkout set` succeeded, so it reports a command that ran — a path that
        // selected 3 and then checked out the whole tree could not produce it.
        Assert.Contains(world.Log,
            line => line.Contains("sparse-checkout restricted the worktree to 3 path(s)"));
        // …and the remote served the partial clone, so nothing announced a lost saving.
        Assert.DoesNotContain(world.Log, line => line.Contains("does not serve partial clones"));
    }

    /// <summary>
    /// A remote that does not serve partial clones does NOT fail the fetch — git warns
    /// "filtering not recognized by server, ignoring" and exits 0 having sent everything. The
    /// ANSWER must be identical, and the lost byte saving must be announced rather than silent:
    /// an unannounced loss of it is how this latency would come back unnoticed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARemoteWithoutPartialClone_AnswersTheSame_AndSaysTheSavingWasLost()
    {
        using var world = await GitWorld.Create(partialCloneCapable: false);

        var snapshot = await world.Client
            .Fetch(world.OriginUrl, "main", null, "", IsListingInput)
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["A/index.json", "A/manifest.lock", "B/index.json"],
            snapshot.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(world.HeadSha, snapshot.CommitSha);
        Assert.Contains(world.Log, line => line.Contains("does not serve partial clones"));
    }

    /// <summary>A filter that matches nothing moves no blobs at all — and still reports the commit
    /// it looked at, so "nothing matched here" stays distinguishable from "I could not look".</summary>
    [Fact(Timeout = 120_000)]
    public async Task AFilterThatMatchesNothing_MovesNoBlobs_AndStillNamesTheCommit()
    {
        using var world = await GitWorld.Create(partialCloneCapable: true);

        var snapshot = await world.Client
            .Fetch(world.OriginUrl, "main", null, "", _ => false)
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Files);
        Assert.Equal(world.HeadSha, snapshot.CommitSha);
        Assert.Contains(world.Log, line => line.Contains("0 of 5 path(s) selected"));
    }

    /// <summary>
    /// 🚨 With a SUBDIRECTORY the two sides of the path speak different languages: the predicate is
    /// handed the subdirectory-RELATIVE path (what every caller filters on), while the sparse
    /// pattern has to be repo-ROOT-relative or it selects nothing. A narrow fetch that confused
    /// them would answer an empty catalog — which is indistinguishable from a repository that
    /// carries no packages. <c>GitHubPackageSource</c> is the caller that passes one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFilteredFetch_UnderASubdirectory_SelectsOnTheRelativePath()
    {
        using var world = await GitWorld.Create(partialCloneCapable: true);

        // "A/" as the subdirectory ⇒ the predicate sees "index.json" / "manifest.lock" / "big.txt".
        var snapshot = await world.Client
            .Fetch(world.OriginUrl, "main", "A", "", path => path == "index.json")
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Equal(["index.json"], snapshot.Files.Select(f => f.Path).ToArray());
        Assert.Equal(world.HeadSha, snapshot.CommitSha);
        // 1 of the repo's 5 paths — the selection ran over the WHOLE tree and kept one.
        Assert.Contains(world.Log, line => line.Contains("1 of 5 path(s) selected"));
    }

    /// <summary>
    /// The control that keeps the change honest: the UNFILTERED fetch is untouched — it still
    /// returns every file, and never narrows. Without this, narrowing the wrong overload would
    /// look exactly like success.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnfilteredFetch_StillReturnsEverything()
    {
        using var world = await GitWorld.Create(partialCloneCapable: true);

        var snapshot = await world.Client
            .Fetch(world.OriginUrl, "main", null, "")
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["A/big.txt", "A/index.json", "A/manifest.lock", "B/index.json", "docs/readme.md"],
            snapshot.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(world.Log, line => line.Contains("path(s) selected"));
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>A seeded origin repository on disk plus a client pointed at it. Five paths, of
    /// which three are listing input — so "3 of 5" is a statement about this repo, not a constant.</summary>
    private sealed class GitWorld : IDisposable
    {
        private readonly string root;
        private readonly IoPoolRegistry pools;
        private readonly RecordingLogger log = new();

        private GitWorld(string root, IoPoolRegistry pools)
        {
            this.root = root;
            this.pools = pools;
            OriginUrl = "";
            HeadSha = "";
            Client = null!;
        }

        public static async Task<GitWorld> Create(bool partialCloneCapable)
        {
            var root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "mw-narrowfetch-" + Guid.NewGuid().ToString("N"))).FullName;
            var world = new GitWorld(root, new IoPoolRegistry());
            var origin = Directory.CreateDirectory(Path.Combine(root, "origin")).FullName;
            var cli = new GitCli(world.pools);

            await Git(cli, origin, ["init", "-q", "-b", "main"]);
            Directory.CreateDirectory(Path.Combine(origin, "A"));
            Directory.CreateDirectory(Path.Combine(origin, "B"));
            Directory.CreateDirectory(Path.Combine(origin, "docs"));
            File.WriteAllText(Path.Combine(origin, "A", "index.json"), """{"nodeType":"Space","name":"A"}""");
            File.WriteAllText(Path.Combine(origin, "A", "manifest.lock"), """{"moduleVersion":"1.0.0"}""");
            File.WriteAllText(Path.Combine(origin, "B", "index.json"), """{"nodeType":"Space","name":"B"}""");
            // The blob the filter excludes — big enough that transferring it would be the point.
            File.WriteAllText(Path.Combine(origin, "A", "big.txt"), new string('x', 2_000_000));
            File.WriteAllText(Path.Combine(origin, "docs", "readme.md"), "readme");
            await Git(cli, origin, ["add", "-A"]);
            await Git(cli, origin, [
                "-c", "user.email=test@meshweaver.local", "-c", "user.name=Test",
                "commit", "-q", "-m", "seed",
            ]);
            await Git(cli, origin, [
                "config", "uploadpack.allowFilter", partialCloneCapable ? "true" : "false",
            ]);
            var head = await Git(cli, origin, ["rev-parse", "HEAD"]);

            world.HeadSha = head.StdOut.Trim();
            world.OriginUrl = "file://" + origin;
            world.Client = new GitProtocolRepoClient(
                new OctokitGitHubRepoClient(world.pools), cli, world.pools, world.log);
            return world;
        }

        public string OriginUrl { get; private set; }
        public string HeadSha { get; private set; }
        public GitProtocolRepoClient Client { get; private set; }
        public IReadOnlyList<string> Log => log.Lines;

        private static async Task<GitCommandResult> Git(GitCli cli, string dir, string[] args)
        {
            var result = await cli.Run(dir, args)
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            Assert.True(result.Ok, $"git {string.Join(' ', args)} failed: {result.Message}");
            return result;
        }

        public void Dispose()
        {
            pools.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup is the OS's */ }
        }
    }

    /// <summary>Captures the client's own account of which path it took. An instance per test —
    /// nothing static, so one test cannot read another's lines.</summary>
    private sealed class RecordingLogger : ILogger<GitProtocolRepoClient>
    {
        // Immutable accumulation rather than a lock-backed list: the client logs from pool threads,
        // so the shared state a concurrent logger keeps is the one place a mutable collection would
        // actually be read while it is being written.
        private ImmutableList<string> lines = ImmutableList<string>.Empty;

        public IReadOnlyList<string> Lines => Volatile.Read(ref lines);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            ImmutableInterlocked.Update(ref lines, (current, l) => current.Add(l), line);
        }
    }
}
