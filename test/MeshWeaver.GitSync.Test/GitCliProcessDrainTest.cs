using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the contract of <see cref="GitCli"/>'s process leaf across the 2026-08-27 rewrite that took
/// the last <c>Task</c> out of <c>MeshWeaver.GitSync</c>.
///
/// <para>The leaf used to start two <c>ReadToEndAsync</c> tasks and unwrap them with
/// <c>GetAwaiter().GetResult()</c> — sync-over-async inside a method whose whole job is to be the
/// synchronous body <see cref="MeshWeaver.Mesh.Threading.IIoPool.InvokeBlocking{T}"/> takes. It now
/// pumps <b>stderr</b> through <c>BeginErrorReadLine</c> while reading <b>stdout</b> to EOF on the
/// pool thread, which keeps both pipes draining concurrently with no Task anywhere.</para>
///
/// <para>Three properties have to survive that, and each one is a way the rewrite could have gone
/// quietly wrong:</para>
/// <list type="number">
///   <item><b>stdout stays byte-exact.</b> Moving stdout to the line pump as well would have been
///   tidier and would have silently rewritten every CRLF in it — and stdout is how
///   <c>GitWorkingTreeService.ShowFile</c> / <c>GitPackageSource.ShowFile</c> read FILE CONTENT out
///   of the object database. A diff view and a manifest parse both read that string.</item>
///   <item><b>Output larger than a pipe buffer still completes.</b> The reason the old code drained
///   both pipes at once in the first place: a 64 KB kernel buffer plus a serial read is a hang, and
///   a hang here parks a Process-pool slot rather than failing.</item>
///   <item><b>A failing command still reports its exit code AND its stderr.</b> stderr is the pipe
///   that changed lanes; every <c>Expect(...)</c> in this assembly turns it into the exception
///   message a user reads.</item>
/// </list>
/// </summary>
public class GitCliProcessDrainTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private GitCli Git => Mesh.ServiceProvider.GetRequiredService<GitCli>();

    /// <summary>
    /// A blob committed with CRLF line endings comes back out of <c>cat-file</c> with its CRLFs
    /// intact. <c>cat-file -p</c> on a blob is the one read git guarantees is verbatim (no eol
    /// conversion, no textconv), so a difference here is this class's doing and nothing else's.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StdOut_PreservesCrlf_Verbatim()
    {
        var repo = await SeedRepo("crlf.txt", "alpha\r\nbeta\r\ngamma\r\n");

        var r = await Git.Run(repo, ["cat-file", "-p", "HEAD:crlf.txt"]).Timeout(30.Seconds()).ToTask();

        Assert.True(r.Ok, $"cat-file failed (exit {r.ExitCode}): {r.Message}");
        // The trailing separator is trimmed by GitCli (unchanged behaviour); the INTERIOR CRLFs are
        // the assertion — a line pump would have delivered "alpha\nbeta\ngamma".
        Assert.Equal("alpha\r\nbeta\r\ngamma", r.StdOut);
    }

    /// <summary>
    /// ~1 MB of stdout — far past any pipe buffer — arrives whole. If the drain ever regresses to a
    /// serial read this test does not fail, it HANGS, which is why it carries its own timeout.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task StdOut_LargerThanPipeBuffer_ArrivesWhole()
    {
        var line = new string('x', 99) + "\n";
        var content = new StringBuilder(1_000_000);
        for (var i = 0; i < 10_000; i++)
            content.Append(line);
        var expected = content.ToString();
        var repo = await SeedRepo("big.txt", expected);

        var r = await Git.Run(repo, ["cat-file", "-p", "HEAD:big.txt"]).Timeout(90.Seconds()).ToTask();

        Assert.True(r.Ok, $"cat-file failed (exit {r.ExitCode}): {r.Message}");
        Assert.Equal(expected.Length - 1, r.StdOut.Length);   // -1: GitCli trims the trailing '\n'
        Assert.Equal(expected.TrimEnd('\n'), r.StdOut);
    }

    /// <summary>
    /// A command git refuses reports a non-zero exit AND the diagnostic git wrote to stderr — the
    /// pipe that moved to the event pump. <c>GitCommandResult.Message</c> prefers stderr, so this is
    /// exactly the string every <c>Expect(...)</c> in this assembly raises.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NonZeroExit_CarriesStdErr()
    {
        var repo = await SeedRepo("only.txt", "hello\n");

        var r = await Git.Run(repo, ["cat-file", "-p", "HEAD:does-not-exist.txt"])
            .Timeout(30.Seconds()).ToTask();

        Assert.False(r.Ok);
        Assert.NotEqual(0, r.ExitCode);
        Assert.NotEmpty(r.StdErr);
        Assert.Equal(r.StdErr, r.Message);
    }

    /// <summary>
    /// A cancelled run raises cancellation instead of reporting a result.
    ///
    /// <para>This is the property the rewrite silently dropped, and review caught it. The pool
    /// cancels this token on unsubscribe and the registration kills the git process tree — which
    /// leaves git with a SIGNAL exit code. Returning that as a <see cref="GitCommandResult"/> makes
    /// every <c>Expect(...)</c> in this assembly raise <c>GitWorkingTreeException</c>: an
    /// unsubscribe would masquerade as "git failed". <c>ReadToEndAsync(ct)</c> used to raise the
    /// <see cref="OperationCanceledException"/> for free; reading synchronously means raising it
    /// explicitly, and this pins that it still happens.</para>
    ///
    /// <para>Asserted on the already-cancelled entry, which is the deterministic half — a
    /// mid-flight kill would need a git command that reliably outlives the test's own scheduling.
    /// Both checks are the same token and the same throw.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void A_cancelled_run_raises_cancellation_and_spawns_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            Git.Exec(Path.GetTempPath(), ["--version"], env: null, cts.Token));
    }

    /// <summary>A fresh repo with one commit containing <paramref name="fileName"/>.</summary>
    private async Task<string> SeedRepo(string fileName, string content)
    {
        var repo = Path.Combine(Path.GetTempPath(), "mw-gitcli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        await RunGit(repo, "-c", "init.defaultBranch=main", "init", ".");
        // autocrlf off so the seeded bytes are the committed bytes on every host — the CRLF case
        // is testing GitCli, not git's own eol machinery.
        await RunGit(repo, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repo, fileName), content, TestContext.Current.CancellationToken);
        await RunGit(repo, "add", "-A");
        await RunGit(repo, "-c", "user.email=t@t.dev", "-c", "user.name=Test", "commit", "-m", "seed");
        return repo;
    }

    private async Task RunGit(string dir, params string[] args)
    {
        var r = await Git.Run(dir, args).Timeout(30.Seconds()).ToTask();
        Assert.True(r.Ok, $"git {string.Join(' ', args)} failed (exit {r.ExitCode}): {r.Message}");
    }
}
