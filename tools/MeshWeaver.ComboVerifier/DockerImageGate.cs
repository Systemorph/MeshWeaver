using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.ComboVerifier;

/// <summary>
/// The production gate seam for <see cref="InstanceComboVerifier"/>: runs <c>mw-plugin-test</c>
/// over the assembled work root AS THE CANDIDATE PORTAL HOST, via docker.
///
/// <para>The contract is the one every node-repo lane has run since MeshWeaver#3022: the COMPOSED
/// GATE HOST — the candidate portal image's <c>/app</c> with the tester CLI laid beside it
/// (<c>.github/scripts/compose-gate-host.sh</c>) — started by the PORTAL image's own <c>dotnet</c>,
/// with <c>--app /app</c> so the gate resolves the portal's framework identity. Not the tester image
/// alone: its <c>/app</c> is a strict subset of the portal's, so content binding a portal-only
/// assembly would fail there as a content error on source nobody changed. Not the portal image
/// alone: it ships no tester — <c>--entrypoint /app/mw-plugin-test</c> in it dies in tini with exit
/// 127 and writes no report, which is how every combo verdict became NotVerifiable (MeshWeaver#3848,
/// run 38688576426). The composed host is mounted read-only at <c>/host</c>; the tester writes its
/// structured report to the work mount (<see cref="GateRunReport.FileName"/>), and the image's
/// non-root user needs write access there, so the work root is made world-writable before the
/// run.</para>
///
/// <para>🚨 It runs in TWO calls, as every node-repo lane does since MeshWeaver#2064: the mesh-free
/// bake (<c>compile</c>), then the mesh gate consuming that bake (<c>--seed</c>). The single fused
/// call made the mesh compile every NodeType itself, which is what killed combo runs at their
/// budget (MeshWeaver#4413, see <see cref="Execute"/>).</para>
///
/// <para>🚨 EVERY docker call is PINNED to <see cref="Platform"/>, and that is not tidiness. A
/// candidate reference is a multi-arch manifest list whose amd64 and arm64 variants carry
/// genuinely different bytes (they resolve different framework build identities — see the bake
/// lane in <c>main-cd.yml</c>), while docker reports the same manifest LIST digest for both. Left
/// unpinned, docker silently picks the HOST's architecture: an operator running the gate on an
/// arm64 laptop gets a Green naming a digest that covers the amd64 bytes the fleet actually runs
/// and that the run never touched — a FALSE PASS that no output distinguishes from a real one.
/// The platform is therefore explicit here and recorded on the verdict
/// (<see cref="ComboVerification.VerifiedPlatform"/>).</para>
///
/// <para>Expected failures — docker absent, pull denied, the run exceeding its budget, a host that
/// cannot emulate the requested platform — come back as <see cref="CandidateGateRun.Error"/>,
/// never as a fault: the verifier folds them into a NotVerifiable verdict. That is the right way
/// to fail: an un-runnable platform reads as "we could not find out", never as "all clear".</para>
///
/// <para>Each docker invocation is a blocking <see cref="Process"/> leaf on the
/// <see cref="IoPoolNames.Process"/> pool, the sanctioned boundary for sync-blocking work; the
/// public surface is <see cref="IObservable{T}"/>.</para>
/// </summary>
public sealed class DockerImageGate(
    IoPoolRegistry pools, TimeSpan runBudget, TextWriter output, string platform, string hostDir,
    ComboModuleComposition? modules = null)
{
    /// <summary>How many trailing characters of the combined docker output the run keeps for
    /// diagnostics.</summary>
    private const int LogTailLength = 8 * 1024;

    /// <summary>The platform every install in the fleet runs. AKS node pools are x86_64 (the CD
    /// bake's arm64 leg is opt-in behind <c>BAKE_ARM64</c>), so a verdict that does not say
    /// otherwise must be about THIS.</summary>
    public const string FleetPlatform = "linux/amd64";

    /// <summary>The files a composed gate host must carry for the gate to be RUNNABLE AS the portal:
    /// the tester CLI and its runtimeconfig (what <c>dotnet</c> starts), and the portal's surface
    /// manifest (what makes the process resolve the portal's identity rather than a fallback). The
    /// same three <c>compose-gate-host.sh</c> asserts as its postcondition.</summary>
    public static readonly ImmutableArray<string> RequiredHostFiles =
        ["mw-plugin-test.dll", "mw-plugin-test.runtimeconfig.json", "meshweaver-surface.manifest"];

    /// <summary>
    /// The process exit code <c>mw-combo-verify</c> returns when its gate host is not runnable
    /// (<see cref="ProbeHost"/>). Distinct from 0 (Green), 1 (Red / NotVerifiable) and 2 (usage),
    /// because it is a different statement: the INSTRUMENT is broken, and nothing about the candidate
    /// was asked. No verdict file is written for it, so nothing is landed on an instance as though
    /// the candidate had been examined.
    /// </summary>
    public const int TesterMissingExitCode = 3;

    /// <summary>The docker platform this gate pins every call to.</summary>
    public string Platform { get; } = platform;

    private IIoPool Pool => pools.Get(IoPoolNames.Process);

    /// <summary>Runs the gate: (candidate image, assembled work root) → one
    /// <see cref="CandidateGateRun"/>. Cold — subscribe to run; cancellation kills the docker
    /// client process so a pool slot is never leaked.</summary>
    public IObservable<CandidateGateRun> Run(string imageRef, string workRoot) =>
        Pool.InvokeBlocking(ct => Execute(imageRef, workRoot, ct));

    /// <summary>
    /// Asserts the INSTRUMENT before anything is assembled: the gate host directory must carry every
    /// one of <see cref="RequiredHostFiles"/>. Emits null when it does, else a message naming the
    /// directory and every missing file.
    ///
    /// <para>🚨 Why a probe, and not a reading of the run's exit code afterwards. A gate with no
    /// tester to start exits 127 and writes no report, which the verifier folds — correctly, for a
    /// TESTER that crashed — into NotVerifiable. That verdict was then LANDED on every live instance
    /// as though the candidate had been examined, and the lane read as "the candidate could not be
    /// verified" when the truth was "the instrument was never there" (MeshWeaver#3848).</para>
    /// </summary>
    public IObservable<string?> ProbeHost() =>
        Pool.InvokeBlocking(_ => MissingHostFiles(hostDir) is { Length: > 0 } missing
            ? $"the gate host '{Path.GetFullPath(hostDir)}' is missing {string.Join(", ", missing)}, "
              + "so the combo gate has no runnable tester — the INSTRUMENT is missing, nothing "
              + "about the candidate was asked, and no verdict is written. Compose it from the "
              + "candidate's release set with .github/scripts/compose-gate-host.sh <portal /app> "
              + "<mw-plugin-test /app> <out> — the portal image ships no tester, and the tester "
              + "image alone is not the platform host."
            : null);

    /// <summary>The <see cref="RequiredHostFiles"/> absent from <paramref name="directory"/>, in
    /// declaration order; empty when the host is runnable.</summary>
    internal static string[] MissingHostFiles(string directory) =>
        [.. RequiredHostFiles.Where(f => !File.Exists(Path.Combine(directory, f)))];

    /// <summary>
    /// Ensures the image is present FOR THE REQUESTED PLATFORM. Null when it is (or the pull
    /// succeeded); otherwise the failed run to report.
    ///
    /// <para>🚨 The presence question is per-PLATFORM, not per-reference. <c>docker image
    /// inspect</c> answers about whichever variant the local store holds, so asking "is it
    /// present?" would report the arm64 copy an earlier run pulled and SKIP the pull of the amd64
    /// bytes we are about to be asked to run. Asking for the architecture instead makes the check
    /// answer the question the run actually depends on. (docker run would pull too, but a separate
    /// pull keeps its output out of the gate log and makes an auth failure a SPEAKING error.)</para>
    /// </summary>
    private CandidateGateRun? EnsurePulled(string imageRef, CancellationToken ct)
    {
        var present = RunDocker(
            ["image", "inspect", "--format", "{{.Os}}/{{.Architecture}}", imageRef], ct,
            TimeSpan.FromMinutes(1));
        var localPlatform = present.ExitCode == 0 ? present.Output.Trim() : null;
        if (string.Equals(localPlatform, Platform, StringComparison.OrdinalIgnoreCase))
            return null;

        output.WriteLine(localPlatform is null
            ? $"pulling {imageRef} ({Platform}) …"
            : $"pulling {imageRef} ({Platform}) — the local copy is {localPlatform} …");
        var pull = RunDocker(PullArgs(imageRef, Platform), ct, runBudget);
        if (pull.TimedOut)
            return new CandidateGateRun
            {
                Platform = Platform,
                Error = $"docker pull '{imageRef}' ({Platform}) exceeded the {runBudget} budget.",
                LogTail = Tail(pull.Output),
            };
        // 🚨 A pull that fails is NOT recoverable by running whatever happens to be local: that is
        // precisely how a run for one architecture gets served another. Report it.
        if (pull.ExitCode != 0)
            return new CandidateGateRun
            {
                Platform = Platform,
                Error = $"docker pull '{imageRef}' for {Platform} failed (exit "
                        + $"{pull.ExitCode}) — the candidate's {Platform} bytes are not "
                        + "available here, so nothing about them was verified.",
                LogTail = Tail(pull.Output),
            };
        return null;
    }

    /// <summary>
    /// Where the run's mesh-free bake is written: beside the work root, never inside it (the tester
    /// discovers packages under the root), with the same sibling convention as the module set
    /// (<c>&lt;work root&gt;.modules</c>).
    /// </summary>
    /// <param name="workRoot">The materialised combo.</param>
    public static string BakeRootFor(string workRoot) => Path.GetFullPath(workRoot) + ".bake";

    /// <summary>
    /// Runs the gate in the SPLIT shape every node-repo lane runs since MeshWeaver#2064: first
    /// <c>mw-plugin-test compile</c>, the mesh-free bake of the whole tree, then the mesh gate
    /// CONSUMING that bake (<c>--seed</c>).
    ///
    /// <para>🚨 <b>Why (MeshWeaver#4413).</b> The combo used to run the FUSED shape: one
    /// <c>mw-plugin-test /work</c> with no seed. The gate host then registered no prebuilt-assembly
    /// consumer, and the MESH compiled every NodeType of every package. The run's own log says so:
    /// <c>NO prebuilt assembly was adopted for 63 installed type(s) because this host registers no
    /// IPrebuiltAssemblyConsumer — … every one of them compiles in-mesh</c>. For <c>Hosting</c>
    /// alone that is 63 per-node compiles of one shared source network, drained one network-sized
    /// Roslyn pass at a time: 15 minutes of the 25-minute budget on a GREEN slot (run 42855183344,
    /// memex: Hosting installed 17:00:16, its last type settled 17:15:41). The gate waits 300 s per
    /// type in path order, so a type the drain reached more than 300 s after its turn began was
    /// recorded as <c>compile-no-verdict</c> (<c>Hosting/ActionList</c>, <c>AiApp</c>,
    /// <c>ApprovalInbox</c>, <c>Backup</c>), and two such waits stacked took the run past 25 minutes,
    /// where it was killed. The producing repository's own gate bakes the same types mesh-free
    /// (Plugins run 42844061345: 190 types in 5 minutes) and its mesh checks of Hosting then take
    /// 4½ minutes. The mesh's job is to CONSUME a bake, never to be the compiler.</para>
    ///
    /// <para>🚨 <b>One budget for both calls.</b> The bake and the gate share <c>runBudget</c>: the
    /// gate gets what the bake left, so splitting the run does not extend the time a slot may
    /// take.</para>
    ///
    /// <para>A bake that seals no identity (<c>framework-mvid.txt</c>) died. That is reported as an
    /// orchestration error (NotVerifiable), never answered by falling back to the fused shape,
    /// which would silently bring back the in-mesh compile of everything. A bake that sealed its
    /// identity but holds no bundle compiled no NodeType; the gate then runs without a seed
    /// (<c>--seed</c> refuses an empty bake, and there is nothing to adopt). A NodeType that failed
    /// to bake is absent from the seed, so the mesh compiles that one type itself and the report
    /// carries its diagnostics, as in the producing repository's lane.</para>
    /// </summary>
    internal CandidateGateRun Execute(string imageRef, string workRoot, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(workRoot);
        var reportFile = Path.Combine(fullRoot, GateRunReport.FileName);
        // A stale report from an earlier run must never masquerade as this run's evidence,
        if (File.Exists(reportFile))
            File.Delete(reportFile);
        // …nor a stale bake as this run's bytes.
        var bakeRoot = BakeRootFor(fullRoot);
        if (Directory.Exists(bakeRoot))
            Directory.Delete(bakeRoot, recursive: true);
        Directory.CreateDirectory(bakeRoot);

        // The image's tester runs as a non-root user and writes its report and its bake to the
        // mounts.
        MakeWorldWritable(fullRoot);
        MakeWorldWritable(bakeRoot);

        var pullFailure = EnsurePulled(imageRef, ct);
        if (pullFailure is not null)
            return pullFailure;

        var digest = ResolveDigest(imageRef, ct);
        var fullHost = Path.GetFullPath(hostDir);
        var started = Clock.GetTimestamp();

        output.WriteLine(
            $"baking the combo with mw-plugin-test compile as {imageRef} ({Platform}), gate host "
            + $"'{fullHost}', over '{fullRoot}' into '{bakeRoot}' (no mesh) …");
        var compile = RunDocker(
            CompileArgs(imageRef, fullRoot, fullHost, bakeRoot, Platform, modules), ct, runBudget);
        if (compile.TimedOut)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Platform = Platform,
                Error = $"the mesh-free bake exceeded the run's {runBudget} budget and was killed — "
                        + "something inside the candidate's compiler did not complete.",
                LogTail = Tail(compile.Output),
            };

        var bake = ReadBake(bakeRoot);
        if (bake == BakeReading.Unsealed)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Platform = Platform,
                Error = $"the mesh-free bake (exit {compile.ExitCode}) sealed no framework identity "
                        + $"in '{bakeRoot}' — the bake died, so there are no bytes to gate and "
                        + "nothing about the candidate was verified.",
                LogTail = Tail(compile.Output),
            };

        var remaining = runBudget - Clock.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Platform = Platform,
                Error = $"the mesh-free bake used the run's whole {runBudget} budget — the gate "
                        + "never started.",
                LogTail = Tail(compile.Output),
            };

        var seed = bake == BakeReading.Bundles ? bakeRoot : null;
        output.WriteLine(
            $"running mw-plugin-test as {imageRef} ({Platform}), gate host '{fullHost}', over "
            + $"'{fullRoot}' "
            + (seed is null
                ? "— the bake compiled no NodeType, so there is no seed to consume …"
                : $"consuming the bake in '{seed}' …"));
        var run = RunDocker(
            RunArgs(imageRef, fullRoot, fullHost, Platform, modules, seed), ct, remaining);
        if (run.TimedOut)
            return new CandidateGateRun
            {
                ImageDigest = digest,
                Platform = Platform,
                Error = $"the gate run exceeded what the bake left of the run's {runBudget} budget "
                        + "and was killed — something inside the candidate did not complete.",
                LogTail = Tail(run.Output),
            };

        return new CandidateGateRun
        {
            ExitCode = run.ExitCode,
            ImageDigest = digest,
            Platform = Platform,
            Report = ReadReport(reportFile),
            LogTail = Tail(run.Output),
        };
    }

    private static void MakeWorldWritable(string directory)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
    }

    /// <summary>What a finished <c>compile --output</c> directory holds.</summary>
    internal enum BakeReading
    {
        /// <summary>No (or an empty) identity file: the bake died before it sealed.</summary>
        Unsealed,

        /// <summary>Sealed, no bundle: the tree holds no NodeType that compiled.</summary>
        NoBundles,

        /// <summary>Sealed, with at least one bundle: a seed the gate consumes.</summary>
        Bundles,
    }

    /// <summary>The identity file every bake seals (the tester's <c>BakeOutput.FrameworkMvidFile</c>).</summary>
    internal const string FrameworkMvidFile = "framework-mvid.txt";

    /// <summary>Reads a bake directory the way <c>mw-plugin-test --seed</c> will: a sealed identity,
    /// then <c>*.zip</c> bundles at its top level.</summary>
    /// <param name="bakeRoot">The directory <c>compile --output</c> wrote.</param>
    internal static BakeReading ReadBake(string bakeRoot)
    {
        var identity = Path.Combine(bakeRoot, FrameworkMvidFile);
        if (!File.Exists(identity) || File.ReadAllText(identity).Trim().Length == 0)
            return BakeReading.Unsealed;
        return Directory.EnumerateFiles(bakeRoot, "*.zip", SearchOption.TopDirectoryOnly).Any()
            ? BakeReading.Bundles
            : BakeReading.NoBundles;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The docker command line — pure, so the contract is pinned by a test
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The <c>docker pull</c> argv, pinned to <paramref name="platform"/>.</summary>
    internal static string[] PullArgs(string imageRef, string platform) =>
        ["pull", "--platform", platform, imageRef];

    /// <summary>
    /// The <c>docker run</c> argv — the node-repo gate lane's container-mode contract (MeshWeaver#3022):
    /// <c>--rm --init --platform … -v host:/host:ro -v root:/work --entrypoint dotnet PORTAL
    /// /host/mw-plugin-test.dll /work --app /app --report /work/…</c>. The PORTAL image's own
    /// <c>dotnet</c> starts the composed host, and <c>--app /app</c> makes the gate resolve the
    /// portal's framework identity — refused before a mesh boots otherwise.
    ///
    /// <para>🚨 ORDER IS THE CONTRACT, not a style choice. Everything before
    /// <paramref name="imageRef"/> is docker's; everything after it is the TESTER's (after the
    /// dll <c>dotnet</c> starts). Move <c>--platform</c> (or any flag) past the image and docker stops
    /// seeing it — the run is no longer pinned to anything. Pinned by
    /// <c>DockerImageGateArgsTest</c>.</para>
    ///
    /// <para>🚨 <paramref name="modules"/> is the combo's MODULE half (MeshWeaver#4413): its root is
    /// mounted read-only at <see cref="ModulesMount"/> and every landed module is passed as
    /// <c>--module</c>, exactly as the node-repo gate lane passes its composed set. Without it the
    /// tester registers no module NodeType at all (its image ships none), and every package whose
    /// content declares a <c>Skill</c> or <c>Agent</c> fails at install for a reason that names
    /// the content.</para>
    ///
    /// <para>🚨 <paramref name="fullSeed"/> is the run's own mesh-free bake (<see cref="CompileArgs"/>),
    /// mounted read-only at <see cref="SeedMount"/> and passed as <c>--seed</c>, so the mesh ADOPTS
    /// the baked assemblies instead of compiling every NodeType itself (MeshWeaver#4413, see
    /// <see cref="Execute"/>).</para>
    /// </summary>
    internal static string[] RunArgs(
        string imageRef, string fullRoot, string fullHost, string platform,
        ComboModuleComposition? modules = null, string? fullSeed = null) =>
    [
        "run", "--rm",
        // --init reaps the tester's children and lets a crashed runtime's SIGABRT end the process
        // (without it the tester is PID 1 and spins instead — #1741); every lane passes it.
        "--init",
        "--platform", platform,
        "-v", $"{fullHost}:{HostMount}:ro",
        "-v", $"{fullRoot}:/work",
        .. modules is { Modules.Length: > 0 }
            ? new[] { "-v", $"{modules.Root}:{ModulesMount}:ro" }
            : [],
        .. fullSeed is null ? [] : new[] { "-v", $"{fullSeed}:{SeedMount}:ro" },
        "--entrypoint", "dotnet",
        imageRef,
        $"{HostMount}/mw-plugin-test.dll",
        "/work",
        "--app", "/app",
        "--report", $"/work/{GateRunReport.FileName}",
        .. modules is null ? [] : modules.TesterArgs(ModulesMount),
        .. fullSeed is null ? [] : new[] { "--seed", SeedMount },
    ];

    /// <summary>
    /// The <c>docker run</c> argv of the mesh-free BAKE, the first half of the split the node-repo
    /// gate lane runs in container mode (<c>run_tester compile "$P_REPO" --app "$P_APP"
    /// --shared-frameworks "$P_SHARED" … --output "$P_BAKE"</c> in <c>node-repo-gate.yml</c>). Same
    /// image, same composed host, same modules as the gate, so the bundles are keyed to the identity
    /// the gate resolves. <c>--app /app</c> and <c>--shared-frameworks</c> name the PORTAL image's
    /// own application directory and implementation frameworks as the reference set. The work root
    /// is mounted exactly as the gate mounts it, and the bundles are written to
    /// <see cref="BakeMount"/>.
    /// </summary>
    internal static string[] CompileArgs(
        string imageRef, string fullRoot, string fullHost, string fullBake, string platform,
        ComboModuleComposition? modules = null) =>
    [
        "run", "--rm",
        "--init",
        "--platform", platform,
        "-v", $"{fullHost}:{HostMount}:ro",
        "-v", $"{fullRoot}:/work",
        "-v", $"{fullBake}:{BakeMount}",
        .. modules is { Modules.Length: > 0 }
            ? new[] { "-v", $"{modules.Root}:{ModulesMount}:ro" }
            : [],
        "--entrypoint", "dotnet",
        imageRef,
        $"{HostMount}/mw-plugin-test.dll",
        "compile", "/work",
        "--output", BakeMount,
        "--app", "/app",
        "--shared-frameworks", PortalSharedFrameworks,
        .. modules is null ? [] : modules.TesterArgs(ModulesMount),
    ];

    /// <summary>Where the run's bake is mounted, writable, for <see cref="CompileArgs"/>.</summary>
    internal const string BakeMount = "/bake";

    /// <summary>Where the run's bake is mounted, read-only, for the gate's <c>--seed</c>.</summary>
    internal const string SeedMount = "/seed";

    /// <summary>The portal image's implementation shared frameworks: the value
    /// <c>node-repo-gate.yml</c> passes as <c>P_SHARED</c> in container mode.</summary>
    internal const string PortalSharedFrameworks = "/usr/share/dotnet/shared";

    /// <summary>Where the combo's composed modules are mounted inside the candidate container.</summary>
    internal const string ModulesMount = "/ext";

    /// <summary>Where the composed gate host is mounted inside the candidate container.</summary>
    internal const string HostMount = "/host";

    /// <summary>The repo digest the ref resolves to (the identity of what was verified), else the
    /// local image id (a locally built candidate has no repo digest), else null.</summary>
    private string? ResolveDigest(string imageRef, CancellationToken ct)
    {
        var repoDigest = RunDocker(
            ["image", "inspect", "--format", "{{index .RepoDigests 0}}", imageRef],
            ct, TimeSpan.FromMinutes(1));
        if (repoDigest.ExitCode == 0 && !string.IsNullOrWhiteSpace(repoDigest.Output))
            return repoDigest.Output.Trim();
        var id = RunDocker(["image", "inspect", "--format", "{{.Id}}", imageRef],
            ct, TimeSpan.FromMinutes(1));
        return id.ExitCode == 0 && !string.IsNullOrWhiteSpace(id.Output) ? id.Output.Trim() : null;
    }

    /// <summary>The structured report, when the tester wrote one; null (never a guess) when it did
    /// not or when it is unparseable — the verifier reports that as NotVerifiable.</summary>
    private GateRunReport? ReadReport(string reportFile)
    {
        if (!File.Exists(reportFile))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GateRunReport>(
                File.ReadAllText(reportFile), InstanceComboAssembler.Json);
        }
        catch (JsonException ex)
        {
            output.WriteLine($"⚠ the gate report at '{reportFile}' is unparseable: {ex.Message}");
            return null;
        }
    }

    /// <summary>One docker invocation: argv, then exit code, combined output, and whether it was
    /// killed on its budget.</summary>
    internal delegate DockerResult DockerCall(
        IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget);

    /// <summary>Test seam: replaces the docker CLI for every call <see cref="Execute"/> makes, so
    /// the split's order, its seed and its shared budget are pinned without a daemon. Null in
    /// production.</summary>
    internal DockerCall? DockerOverride { get; init; }

    /// <summary>The clock the shared budget is measured on; a test advances it.</summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    private DockerResult RunDocker(IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget) =>
        DockerOverride is { } fake ? fake(args, ct, budget) : Docker(args, ct, budget);

    private DockerResult Docker(IReadOnlyList<string> args, CancellationToken ct, TimeSpan budget)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var combined = new StringBuilder();
        var sync = new object();

        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // Cancellation kills the docker client (which tears down the container via --rm) so a
            // pool slot is never leaked on unsubscribe.
            using var reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
            });
            if (!p.WaitForExit((int)budget.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                p.WaitForExit();
                return new DockerResult(-1, Combined(), TimedOut: true);
            }
            // Flush the async readers.
            p.WaitForExit();
            return new DockerResult(p.ExitCode, Combined(), TimedOut: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // docker itself is not runnable here — a speaking orchestration error, not a crash.
            return new DockerResult(-1,
                $"could not start 'docker {string.Join(' ', args)}': {ex.Message}", TimedOut: false);
        }

        void Append(string? line)
        {
            if (line is null)
                return;
            lock (sync)
            {
                combined.AppendLine(line);
                output.WriteLine($"    {line}");
            }
        }

        string Combined()
        {
            lock (sync)
                return combined.ToString();
        }
    }

    private static string Tail(string text) =>
        text.Length <= LogTailLength ? text : text[^LogTailLength..];

    /// <summary>What one docker invocation returned.</summary>
    internal sealed record DockerResult(int ExitCode, string Output, bool TimedOut);
}
