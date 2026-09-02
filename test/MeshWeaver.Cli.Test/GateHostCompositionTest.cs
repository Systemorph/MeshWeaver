using System.Security.Cryptography;
using MeshWeaver.Cli;
using Xunit;

namespace MeshWeaver.Cli.Test;

/// <summary>
/// 🚨 <b><c>memex build plugin</c> composes the PORTAL's reference set, by the LANES' rules</b>
/// (#3113, the CLI half of #3022/#3071).
///
/// <para>The tester image's <c>/app</c> is a strict subset of the portal's — 88 vs 219 assemblies on
/// 3.0.0-rc9.ci.7534 — so a build that ran straight from the tester's entrypoint could not compile
/// content binding <c>MeshWeaver.Maps</c>, <c>.AI</c> or <c>.ContentCollections.Indexing</c>. On
/// MeshWeaver.Manufacturing#48 that surfaced as <c>CS0234 … 'Maps' does not exist in the namespace
/// 'MeshWeaver'</c> against <c>AppleMaps/Gallery</c> and <c>Cornerstone/Pricing</c>: a CONTENT-shaped
/// failure with an INFRASTRUCTURE cause, on source nobody had changed.</para>
///
/// <para>Every assertion here is over a PURE seam. The failure being defended against is not a crash
/// but a run that SUCCEEDS against the wrong reference set, which is indistinguishable from a real
/// pass anywhere except in the argv and in which rules composed the host — so those are what is
/// pinned, and neither needs a docker daemon CI would have to skip.</para>
/// </summary>
public class GateHostCompositionTest
{
    private const string Portal = "meshweaver.azurecr.io/memex-portal-ai@sha256:0123456789abcdef";
    private const string Tester = "meshweaver.azurecr.io/mw-plugin-test@sha256:fedcba9876543210";
    private const string Host = "/tmp/memex-host-1/gate-host";

    // ── the composition rules are ONE implementation ──────────────────────────────────────────

    /// <summary>
    /// 🚨 <b>THE DRIFT GUARD.</b> The CLI carries <c>.github/scripts/compose-gate-host.sh</c> — the
    /// very file the reusable lanes fetch from the platform at their pinned ref — not a C# port of
    /// its rules. Its ordering (portal first and complete; tester files added only where the portal
    /// has none; the tester's manifest and <c>deps.json</c> never copied) and its fail-closed
    /// refusals were each derived from a measured incident, and a second copy of them would be the
    /// trap this repository has paid for repeatedly: three defects in one day were N drifted copies
    /// of a rule whose own comment claimed it had one.
    ///
    /// <para>Byte equality, not "contains the same rules": a paraphrase is a copy.</para>
    /// </summary>
    [Fact]
    public void TheEmbeddedComposeScript_IsTheLanesScript_ByteForByte()
    {
        var onDisk = Path.Combine(FindRepoRoot(), ".github", "scripts", "compose-gate-host.sh");
        Assert.True(File.Exists(onDisk),
            $"{onDisk} is missing — it is the ONE implementation of the gate-host composition rules, "
            + "fetched by node-repo-gate.yml / node-repo-publish-bake.yml / node-repo-module-pack.yml "
            + "and embedded by MeshWeaver.Cli.");

        var embedded = GateHost.ComposeScriptBytes();
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(onDisk))),
            Convert.ToHexString(SHA256.HashData(embedded)));
    }

    /// <summary>
    /// The CLI extracts that script to run it — the extraction is the whole of its composition, so a
    /// silently empty or partial write would leave the build with no portal reference set at all.
    /// </summary>
    [Fact]
    public void ExtractComposeScript_WritesTheScriptWhereTheCliRunsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mw-cli-compose-{Guid.NewGuid():N}");
        try
        {
            var path = GateHost.ExtractComposeScript(dir);
            Assert.Equal(Path.Combine(dir, "compose-gate-host.sh"), path);
            Assert.Equal(GateHost.ComposeScriptBytes(), File.ReadAllBytes(path));
            // The lanes `chmod +x` the script they fetch, and so must this: the script re-invokes
            // itself by path to prove its own rules, so a 0644 copy cannot run its self-test.
            if (!OperatingSystem.IsWindows())
                Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute),
                    "the extracted compose script must be executable");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 The extracted script RUNS, and its rules FIRE. Byte equality proves the CLI stores the
    /// lanes' file; this proves the CLI can execute it — the same <c>--self-test</c> the platform's
    /// own preflight runs on <c>.github/scripts/compose-gate-host.sh</c>, driven from the bytes the
    /// CLI would actually put on disk. It asserts that the portal wins a shared file, that
    /// tester-only files ride, that the tester's manifest and <c>deps.json</c> do not, and that every
    /// refusal is non-zero.
    ///
    /// <para>No skip: <c>bash</c> is a hard requirement of this verb (it is how the host gets
    /// composed at all), so its absence is a failure here exactly as it is a failure in the build.
    /// A test that skipped instead would render the same green tick as a passing one.</para>
    /// </summary>
    [Fact]
    public async Task TheExtractedScript_PassesItsOwnSelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mw-cli-selftest-{Guid.NewGuid():N}");
        try
        {
            var script = GateHost.ExtractComposeScript(dir);
            var psi = new System.Diagnostics.ProcessStartInfo("bash")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("--self-test");

            using var process = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(process);
            var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.True(process.ExitCode == 0,
                $"the embedded compose-gate-host.sh failed its own self-test:\n{stdout}\n{stderr}");
            Assert.Contains("self-test: OK", stdout);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ── the temp trees are removed, and cleanup cannot decide the verdict ─────────────────────

    /// <summary>
    /// Two <c>/app</c> extractions plus the composed host is roughly a gigabyte, and the temp path
    /// carries the process id — so nothing ever reuses it and stale copies accumulate until the
    /// disk is full.
    /// </summary>
    [Fact]
    public async Task DiscardTree_RemovesTheTree()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mw-cli-discard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "portal-app", "modules", "X"));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "portal-app", "modules", "X", "X.dll"), "bytes",
            TestContext.Current.CancellationToken);

        var notes = new StringWriter();
        await GateHost.DiscardTree(dir, notes);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(string.Empty, notes.ToString());
    }

    /// <summary>A path that is already gone is a no-op, not a throw — every early refusal in the
    /// verb returns before anything is extracted and still runs the cleanup.</summary>
    [Fact]
    public async Task DiscardTree_OnAnAbsentPath_IsSilent()
    {
        var notes = new StringWriter();
        await GateHost.DiscardTree(
            Path.Combine(Path.GetTempPath(), $"mw-cli-absent-{Guid.NewGuid():N}"), notes);
        Assert.Equal(string.Empty, notes.ToString());
    }

    /// <summary>
    /// 🚨 A tree that CANNOT be removed must not decide the build's verdict — and must not vanish
    /// in silence either. Arranged for real by taking write permission off the parent, so the
    /// delete genuinely fails rather than being simulated.
    /// </summary>
    [Fact]
    public async Task DiscardTree_ThatCannotBeRemoved_ReportsAndDoesNotThrow()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "arranged through unix directory permissions; the Windows model differs");
        // SkipWhen already ended the test above; CA1416 cannot see that a throwing helper is a
        // platform guard, so this is the check the analyzer reads. Not a second policy.
        if (OperatingSystem.IsWindows()) return;

        var parent = Path.Combine(Path.GetTempPath(), $"mw-cli-locked-{Guid.NewGuid():N}");
        var child = Path.Combine(parent, "gate-host");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(
            Path.Combine(child, "mw-plugin-test.dll"), "bytes", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var notes = new StringWriter();
            await GateHost.DiscardTree(child, notes);   // must not throw

            Assert.Contains("could not remove the temporary directory", notes.ToString());
            Assert.Contains(child, notes.ToString());
        }
        finally
        {
            File.SetUnixFileMode(
                parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(parent, recursive: true);
        }
    }

    // ── the argv: what the two stages actually run ────────────────────────────────────────────

    /// <summary>
    /// 🚨 <b>THE REGRESSION GUARD for #3113.</b> The run starts the PLATFORM image's own
    /// <c>dotnet</c> on the tester CLI taken from the COMPOSED HOST. Point the entrypoint back at
    /// the tester image (<c>--entrypoint /app/mw-plugin-test &lt;tester&gt;</c>, what this verb did
    /// until now) and every assertion below fails — which is the only way the wrong reference set
    /// is observable, since the run itself would still exit 0 on content that binds nothing
    /// portal-only.
    /// </summary>
    [Fact]
    public void EveryRun_StartsThePlatformImagesDotnet_OnTheComposedHostsTesterCli()
    {
        var args = ImageRunner.ComposedHostRunArgs(Portal, Host, "1001:1001", [], [], ["/repo"]);

        var image = Array.IndexOf(args, Portal);
        Assert.True(image > 0, "the PLATFORM image reference must appear in the argv");
        Assert.DoesNotContain(args, a => a.Contains("mw-plugin-test@", StringComparison.Ordinal));

        // Everything before the image is docker's; everything after it is the tool's. A flag that
        // drifts past the image is not a flag any more — docker never sees it and mw-plugin-test
        // takes it as an unknown argument, so the run silently stops being composed.
        var docker = args[..image];
        Assert.Contains("--rm", docker);
        Assert.Contains("--init", docker);
        AssertAdjacent(docker, "--entrypoint", "dotnet");
        AssertAdjacent(docker, "-v", $"{Host}:/host:ro");

        // The ENTRY ASSEMBLY must be the one on the composed host: the framework identity and the
        // TPA are both read from the entry assembly's directory.
        Assert.Equal("/host/mw-plugin-test.dll", args[image + 1]);
        Assert.Equal("/repo", args[image + 2]);
    }

    /// <summary>
    /// The portal image runs as root, so a run that did not adopt the invoking user's uid would
    /// leave every bundle it writes root-owned and unrewritable by the rest of the caller's job —
    /// the reason node-repo-publish-bake.yml and node-repo-module-pack.yml both pass
    /// <c>$(id -u):$(id -g)</c>. <c>HOME</c> rides with it: a uid the image never provisioned has no
    /// home, and the runtime's probes would fall on the image's read-only paths.
    /// </summary>
    [Fact]
    public void TheRun_AdoptsTheInvokingUser_AndGivesItAWritableHome()
    {
        var args = ImageRunner.ComposedHostRunArgs(Portal, Host, "1001:123", [], [], ["/repo"]);
        AssertAdjacent(args[..Array.IndexOf(args, Portal)], "--user", "1001:123");
        AssertAdjacent(args[..Array.IndexOf(args, Portal)], "-e", "HOME=/tmp");
    }

    /// <summary>On a platform with no uid mapping the flag is omitted rather than invented.</summary>
    [Fact]
    public void WithoutAnInvokingUser_NoUserFlagIsInvented()
    {
        var args = ImageRunner.ComposedHostRunArgs(Portal, Host, user: null, [], [], ["/repo"]);
        Assert.DoesNotContain("--user", args);
        AssertAdjacent(args[..Array.IndexOf(args, Portal)], "-e", "HOME=/tmp");
    }

    /// <summary>Mounts and environment reach docker, in docker's half of the argv.</summary>
    [Fact]
    public void MountsAndEnvironment_LandBeforeTheImage()
    {
        var args = ImageRunner.ComposedHostRunArgs(
            Portal, Host, "0:0", ["/w/repo:/repo", "/w/bake:/seed:ro"], ["MW_INSTALL_DIFF=1"],
            ["/repo", "--seed", "/seed"]);
        var docker = args[..Array.IndexOf(args, Portal)];
        AssertAdjacent(docker, "-v", "/w/repo:/repo");
        AssertAdjacent(docker, "-v", "/w/bake:/seed:ro");
        AssertAdjacent(docker, "-e", "MW_INSTALL_DIFF=1");
    }

    /// <summary>
    /// The reference set the compile stage names: the platform's <c>/app</c> plus its IMPLEMENTATION
    /// frameworks — what a portal's runtime compile sees. Both halves, because <c>--app</c> and
    /// <c>--shared-frameworks</c> are refused separately by the tester ("they go together").
    /// </summary>
    [Fact]
    public void TheSharedFrameworksRoot_IsThePlatformsImplementationRuntime() =>
        Assert.Equal("/usr/share/dotnet/shared", GateHost.SharedFrameworks);

    // ── the refusal: the tester passed as the platform ────────────────────────────────────────

    /// <summary>
    /// 🚨 The one wrong value nothing else can refuse: the TESTER handed in as
    /// <c>--platform-image</c>, which would silently restore the very reference-set gap the argument
    /// closes. node-repo-gate.yml refuses it by name; so does this.
    /// </summary>
    [Theory]
    [InlineData("meshweaver.azurecr.io/mw-plugin-test:3.0.0-rc9.ci.7574", true)]
    [InlineData("meshweaver.azurecr.io/MW-Plugin-Test@sha256:abc", true)]
    [InlineData("meshweaver.azurecr.io/memex-portal-ai@sha256:abc", false)]
    [InlineData("ghcr.io/systemorph/memex-portal-ai:main", false)]
    public void ATesterReference_IsRecognisedAsTheWrongPlatformImage(string image, bool isTester) =>
        Assert.Equal(isTester, GateHost.NamesTheTesterImage(image));

    private static void AssertAdjacent(string[] args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == flag && args[i + 1] == value)
                return;
        Assert.Fail($"expected '{flag} {value}' in: {string.Join(' ', args)}");
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
