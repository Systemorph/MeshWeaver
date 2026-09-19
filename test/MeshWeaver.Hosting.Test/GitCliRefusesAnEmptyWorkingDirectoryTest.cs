using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A git command with no working directory is REFUSED, never run in the host's own directory.
///
/// <para><see cref="System.Diagnostics.ProcessStartInfo.WorkingDirectory"/> falls back to the
/// CURRENT PROCESS's directory when it is empty, so a caller that computed no path does not fail —
/// it runs git against whatever directory the host happens to be in. Measured 2026-09-15: a test
/// host running from a checkout of this repository left <c>init</c>/<c>seed</c> commits on that
/// checkout's <c>main</c> and set <c>core.bare = true</c> on its shared config, after which every
/// worktree of it answered <i>"this operation must be run in a work tree"</i>. Nothing in the
/// symptom named git, and nothing named the caller.</para>
///
/// <para>The control below is the half that makes the refusal meaningful: a REAL directory still
/// runs git, so this is a guard on the caller's mistake and not a new restriction on the tool.</para>
/// </summary>
public class GitCliRefusesAnEmptyWorkingDirectoryTest
{
    private static GitCli NewCli(out IoPoolRegistry pools)
    {
        pools = new IoPoolRegistry();
        return new GitCli(pools);
    }

    [Theory(Timeout = 120_000)]   // a constant is required in an attribute — see AFrozenContentCiIsReportedTest
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyWorkingDirectory_IsRefused_NamingWhatWouldHaveHappened(string workingDir)
    {
        var cli = NewCli(out var pools);
        using var _ = pools;

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            cli.Run(workingDir, ["status"]).FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.Contains("no working directory", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("inherit", refusal.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task ADirectoryThatIsNotThere_IsRefused_ByPath()
    {
        var cli = NewCli(out var pools);
        using var _ = pools;
        var absent = Path.Combine(Path.GetTempPath(), "mw-gitcli-absent-" + Guid.NewGuid().ToString("N"));

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() =>
            cli.Run(absent, ["status"]).FirstAsync().Await(TestContext.Current.CancellationToken));

        Assert.Contains(absent, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("does not", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a real directory still RUNS git — the refusal above is about the
    /// caller's missing path, never about the command.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARealDirectory_StillRunsGit()
    {
        var cli = NewCli(out var pools);
        using var _ = pools;
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "mw-gitcli-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var result = await cli.Run(dir, ["rev-parse", "--is-inside-work-tree"])
                .FirstAsync().Await(TestContext.Current.CancellationToken);

            // git RAN: a directory that is not a repository answers with git's own refusal, which
            // is a verdict from the process rather than from the guard above.
            Assert.False(result.Ok);
            Assert.Contains("not a git repository", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp cleanup is the OS's */ }
        }
    }
}
