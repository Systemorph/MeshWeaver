#pragma warning disable CS1591

using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Guard for the platform-file reader (<c>.github/scripts/mw-core-file.sh</c>, installed by
/// <c>.github/actions/core-file</c>) — Doc/Architecture/CiRestBudget. It lands BEFORE the lanes use it:
/// a reusable lane names the action at <c>@main</c>, which GitHub resolves before any step runs, so
/// the action has to exist on <c>main</c> first.
///
/// <para>The reader runs as the first step of every job that reads a platform file, so a regression
/// in it breaks every lane at once — and nothing in a satellite's own CI would say why.</para>
/// </summary>
public class PlatformFileReaderGuard
{
    /// <summary>The reader itself, against a LOCAL repository (no network): it returns the exact bytes
    /// at a branch, a tag and a full sha; it pins one commit per (job, ref) so a moving branch cannot
    /// hand two reads of one job two trees; and it refuses — non-zero, named — an abbreviated sha and
    /// a path that does not exist, never printing something else.</summary>
    [Fact]
    public void TheReader_ReturnsTheBytesAtTheRef_AndRefusesWhatItCannotRead()
    {
        var work = Directory.CreateTempSubdirectory("mw-core-file-");
        try
        {
            var origin = Path.Combine(work.FullName, "origin");
            Directory.CreateDirectory(Path.Combine(origin, ".github", "scripts"));
            Git(origin, "init", "-q", "-b", "main");
            Git(origin, "config", "user.email", "guard@example.invalid");
            Git(origin, "config", "user.name", "guard");
            Git(origin, "config", "commit.gpgsign", "false");
            Git(origin, "config", "uploadpack.allowFilter", "true");
            Git(origin, "config", "uploadpack.allowAnySHA1InWant", "true");
            File.WriteAllText(Path.Combine(origin, ".github", "scripts", "x.py"), "first\n");
            Git(origin, "add", "-A");
            Git(origin, "commit", "-q", "-m", "one");
            Git(origin, "tag", "v1");
            var first = Git(origin, "rev-parse", "HEAD").Trim();

            var temp = Path.Combine(work.FullName, "runner-temp");
            Directory.CreateDirectory(temp);
            var url = new Uri(origin).AbsoluteUri;

            var atMain = Read(temp, url, "main", ".github/scripts/x.py");
            Assert.True(atMain.Exit == 0, atMain.Stderr);
            Assert.Equal("first\n", atMain.Stdout);

            // main moves; the SAME job (same RUNNER_TEMP) keeps reading the commit it fetched first.
            File.WriteAllText(Path.Combine(origin, ".github", "scripts", "x.py"), "second\n");
            Git(origin, "commit", "-q", "-am", "two");
            Assert.Equal("first\n", Read(temp, url, "main", ".github/scripts/x.py").Stdout);
            // …and a new job reads the new tip.
            var nextJob = Path.Combine(work.FullName, "next-job");
            Directory.CreateDirectory(nextJob);
            Assert.Equal("second\n", Read(nextJob, url, "main", ".github/scripts/x.py").Stdout);

            Assert.Equal("first\n", Read(temp, url, "v1", ".github/scripts/x.py").Stdout);
            Assert.Equal("first\n", Read(temp, url, first, ".github/scripts/x.py").Stdout);

            // An abbreviated sha is refused by SHAPE — even when a same-named BRANCH exists, which git
            // would otherwise resolve and read (an unrelated tree under a sha-looking name).
            var prefix = first[..10];
            Git(origin, "branch", prefix);
            var abbreviated = Read(temp, url, prefix, ".github/scripts/x.py");
            Assert.NotEqual(0, abbreviated.Exit);
            Assert.Equal("", abbreviated.Stdout);
            Assert.Contains("ABBREVIATED sha", abbreviated.Stderr, StringComparison.Ordinal);
            // Two refs a character substitution would conflate (`feature/a`, `feature_a`) read their
            // OWN commits in one job — the cache key is the exact ref, hashed.
            Git(origin, "checkout", "-q", "-b", "feature/a");
            File.WriteAllText(Path.Combine(origin, ".github", "scripts", "x.py"), "slash\n");
            Git(origin, "commit", "-q", "-am", "slash");
            Git(origin, "checkout", "-q", "-b", "feature_a");
            File.WriteAllText(Path.Combine(origin, ".github", "scripts", "x.py"), "underscore\n");
            Git(origin, "commit", "-q", "-am", "underscore");
            Git(origin, "checkout", "-q", "main");
            Assert.Equal("slash\n", Read(temp, url, "feature/a", ".github/scripts/x.py").Stdout);
            Assert.Equal("underscore\n", Read(temp, url, "feature_a", ".github/scripts/x.py").Stdout);
            // …and the same name, spelled as the named ref it is, still reads.
            Assert.Equal("second\n", Read(Path.Combine(work.FullName, "named-ref-job"), url, $"refs/heads/{prefix}", ".github/scripts/x.py").Stdout);

            var missing = Read(temp, url, "main", ".github/scripts/absent.py");
            Assert.NotEqual(0, missing.Exit);
            Assert.Equal("", missing.Stdout);
            Assert.Contains("does not exist", missing.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The budget mode runs as the FIRST step of every job that installs the reader, so a
    /// parser or warning regression would break every lane: driven offline against a stubbed
    /// <c>curl</c> — a normal reading, the 80 % boundary (below and at), an unanswered request and no
    /// token. It never fails the job: an unreadable budget is said, not fatal.</summary>
    [Theory]
    [InlineData("{\"resources\":{\"core\":{\"limit\":1000,\"used\":412,\"remaining\":588,\"reset\":1791386342}}}", true, "REST budget of this job's token: 412 of 1000 used, resets ", false)]
    [InlineData("{\"resources\":{\"core\":{\"limit\":1000,\"used\":799,\"remaining\":201,\"reset\":1791386342}}}", true, "799 of 1000 used", false)]
    [InlineData("{\"resources\":{\"core\":{\"limit\":1000,\"used\":800,\"remaining\":200,\"reset\":1791386342}}}", true, "::warning title=REST budget nearly spent::REST budget of this job's token: 800 of 1000 used", true)]
    [InlineData(null, true, "REST budget: /rate_limit did not answer — not read", false)]
    [InlineData("{\"resources\":{\"core\":", true, "REST budget: /rate_limit answered something unreadable — not read", false)]
    [InlineData("[1,2,3]", true, "REST budget: /rate_limit answered something unreadable — not read", false)]
    [InlineData("{}", false, "REST budget: no token in this step — not read", false)]
    public void TheBudgetMode_ReadsTheBudget_WarnsAtEightyPercent_AndNeverFailsTheJob(
        string? body, bool withToken, string expected, bool warns)
    {
        var work = Directory.CreateTempSubdirectory("mw-core-file-budget-");
        try
        {
            var bin = Path.Combine(work.FullName, "bin");
            Directory.CreateDirectory(bin);
            var stub = Path.Combine(bin, "curl");
            // A stub `curl`: prints the canned /rate_limit body, or fails as an unanswered request does.
            File.WriteAllText(stub, body is null
                ? "#!/usr/bin/env bash\nexit 28\n"
                : "#!/usr/bin/env bash\ncat <<'JSON'\n" + body + "\nJSON\n");
            using (var chmod = Process.Start("chmod", ["+x", stub]) ?? throw new InvalidOperationException("chmod could not be started"))
            {
                chmod.WaitForExit();
                Assert.Equal(0, chmod.ExitCode);
            }

            var psi = new ProcessStartInfo("bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(Path.Combine(FindRepoRoot(), ".github", "scripts", "mw-core-file.sh"));
            psi.ArgumentList.Add("--budget");
            psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            psi.Environment.Remove("GH_TOKEN");
            psi.Environment.Remove("GITHUB_TOKEN");
            if (withToken)
                psi.Environment["GH_TOKEN"] = "stub-token";
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"the budget mode must never fail the job (exit {process.ExitCode}): {stderr}");
            Assert.Contains(expected, stdout, StringComparison.Ordinal);
            Assert.Equal(warns, stdout.Contains("::warning", StringComparison.Ordinal));
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static (int Exit, string Stdout, string Stderr) Read(string runnerTemp, string url, string gitRef, string path)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(FindRepoRoot(), ".github", "scripts", "mw-core-file.sh"));
        psi.ArgumentList.Add(gitRef);
        psi.ArgumentList.Add(path);
        psi.Environment["RUNNER_TEMP"] = runnerTemp;
        psi.Environment["MW_CORE_GIT_URL"] = url;
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // A partial clone over file:// needs the protocol allowed for the lazy blob fetch.
        psi.Environment["GIT_ALLOW_PROTOCOL"] = "file";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("bash could not be started");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
