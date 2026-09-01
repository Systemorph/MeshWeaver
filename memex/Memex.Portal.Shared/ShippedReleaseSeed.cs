using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared;

/// <summary>
/// Boot seed that ships compiled <c>Release</c>s WHEREVER we ship code NodeTypes — the
/// documentation partition AND every shipped sample/platform partition (<c>ACME</c>, <c>FutuRe</c>,
/// <c>Northwind</c>, <c>Cornerstone</c>, <c>MeshWeaver</c>, …). At startup it finds every code
/// NodeType in those partitions that has no usable build yet and triggers a release for it — under
/// the <b>System</b> identity, exactly like the per-NodeType first-build kickoff.
///
/// <para><b>Why:</b> a shipped partition's NodeTypes otherwise compile ON-DEMAND the first time a
/// user navigates to them — and that on-demand compile path (its <c>_Activity/compile-*</c> writes)
/// is exactly what storm-failed on prod 2026-06-18 when the activity write ran without a writer
/// identity. Pre-building the releases at provision/deploy time as System means the runtime path is
/// always a cache hit: no on-demand compile, no phantom <c>_Activity/compile-*</c> subscribe storm.
/// Several shipped partitions (<c>Doc</c>) are read-only — only a System-credentialed compile can
/// fill their cache and write the <c>Release</c> node, which is precisely the credential split this
/// workflow establishes.</para>
///
/// <para>Each release runs entirely as System (no <c>RequestedReleaseBy</c>), so the compile fills
/// the cache and the <c>Release</c> node is created even on a read-only partition. Idempotent: a
/// NodeType that already has a usable build is skipped, so re-boots are cheap. Fire-and-forget off
/// the thread pool — never blocks host startup.</para>
///
/// <para><b>Opt-in per deployment</b> (<c>ShippedRelease:PreseedEnabled</c>, default off): even the
/// "skip already-built" pass stream-reads every code NodeType in every shipped partition — a boot
/// subscribe storm on a loaded mesh. By default only the partitions a deployment names in
/// <c>ShippedRelease:ExtraPreseedPartitions</c> are seeded; everything else compiles lazily on
/// first access.</para>
/// </summary>
public static class ShippedReleaseSeed
{
    /// <summary>
    /// The partitions whose code NodeTypes ship pre-built releases. The platform documentation plus
    /// the bundled sample spaces — i.e. every partition we ship NodeTypes in. User/Space partitions
    /// created at runtime are NOT here: those NodeTypes are authored by users and released by them
    /// (the <see cref="Permission.Compile"/>-gated "Create Release" button).
    /// </summary>
    public static readonly IReadOnlyList<string> ShippedPartitions =
    [
        "Doc", "ACME", "FutuRe", "Northwind", "Cornerstone", "MeshWeaver"
    ];

    // ═════════════════════════════════════════════════════════════════════
    // Platform-startup anchor (Admin partition) — see AccessControl.md → "The
    // Admin partition" and ActivityNodeGuard. Platform-startup activities must
    // hang off a REAL node in the Admin partition (a node holding the installed
    // platform version), never the top-level mesh hub (a bare _Activity/{id} has
    // no partition hub to route to → the RoutingGrain NotFound-storms).
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>The Admin partition that holds platform-level data (schema <c>admin</c>, migration-provisioned).</summary>
    public const string AdminPartition = "Admin";

    /// <summary>Id of the platform-version node within the Admin partition.</summary>
    public const string PlatformVersionId = "PlatformVersion";

    /// <summary>Full path of the platform-version node: the canonical Admin-partition anchor for startup activities.</summary>
    public const string PlatformVersionNodePath = $"{AdminPartition}/{PlatformVersionId}";

    /// <summary>
    /// The installed platform version. The FULL run-numbered version (<c>3.0.0-rc3.ci.N</c>) is no
    /// longer compiled into any assembly — CI builds are commit-deterministic so the bake identity
    /// holds across CI jobs (#1660 WS3) — so this reads the
    /// <see cref="MeshWeaver.Mesh.PlatformBuildInfo.PlatformVersionEnvironmentVariable"/> the
    /// container publish injects FIRST (the self-updater's version comparison against registry
    /// tags depends on the run number), then falls back to the build assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/> (<c>PlatformVersion+&lt;sha&gt;</c> on a
    /// CI build), the numeric assembly version, then <c>"unknown"</c>.
    ///
    /// <para>🚨 <b>Build assembly, not entry assembly.</b> Reading
    /// <see cref="Assembly.GetEntryAssembly"/> here reported <c>1.0.0</c> on the deployed portal for
    /// a week: the portal executable moved to MeshWeaver.Plugins (2026-08-25), which defines no
    /// <c>Version</c>, so the host stopped being part of this build. Because
    /// <c>VersionSelect.IsNewer</c> compares registry tags against THIS value, every tag then looked
    /// newer forever — the install could never reach "up to date" and re-rolled on every check
    /// floor. <see cref="MeshWeaver.Mesh.PlatformBuildInfo"/> owns the selection so this page and
    /// <c>/api/version</c> cannot answer differently.</para>
    /// </summary>
    public static string InstalledPlatformVersion => MeshWeaver.Mesh.PlatformBuildInfo.PlatformVersion;

    /// <summary>The GitHub repository the platform is built from.</summary>
    public const string RepositoryUrl = "https://github.com/Systemorph/MeshWeaver";

    /// <summary>
    /// The git commit SHA this build was produced from — baked in as <c>AssemblyMetadata("CommitHash")</c>
    /// by the <c>AddCommitHashMetadata</c> target in <c>Directory.Build.props</c> (from the SDK's
    /// <c>SourceRevisionId</c> or the CI <c>GITHUB_SHA</c>). Null when the build carried no source-control
    /// info (a git-less source drop) — the About page then falls back to the version string alone.
    /// Read off the same build assembly the version comes from, so "which build" and "which commit"
    /// can never name two different builds.
    /// </summary>
    public static string? CommitHash => MeshWeaver.Mesh.PlatformBuildInfo.CommitHash;

    /// <summary>Link to the GitHub commit this build was produced from, or null when the SHA is unknown.</summary>
    public static string? CommitUrl => CommitHash is { } sha ? $"{RepositoryUrl}/commit/{sha}" : null;

    /// <summary>
    /// When THIS build started serving, in UTC — the process start time.
    ///
    /// <para>This is the deployment moment as a user experiences it. A version answers "which
    /// build", never "since when", and those are different questions: after a roll the version may
    /// be unchanged (a re-deploy of the same image) or have changed minutes ago, and only a
    /// timestamp separates "this has been live for a week" from "this landed while I was reading".
    /// It is the question that goes unanswered every time someone asks whether a portal actually
    /// rolled forward.</para>
    ///
    /// <para>Process start rather than a build/compile timestamp on purpose: a pod runs the image
    /// it was given, and a viewer wants to know when the thing answering them began answering.
    /// Under Kubernetes a rollout replaces the pod, so process start IS the roll. Read once and
    /// cached — it cannot change within a process, and re-reading per render would be a syscall
    /// for a constant.</para>
    ///
    /// <para>UTC by contract; every surface renders it in the VIEWER's zone via
    /// <c>DisplayTimeExtensions</c>, never as a bare UTC string.</para>
    /// </summary>
    public static DateTimeOffset StartedAtUtc { get; } = ReadProcessStartUtc();

    private static DateTimeOffset ReadProcessStartUtc()
    {
        // WASM has no process to ask, and no deploy of its own to report — the build it runs is the
        // one the server handed it. Guarded rather than caught: CA1416 is right that this is not a
        // runtime accident to swallow.
        if (OperatingSystem.IsBrowser()) return default;

        try
        {
            return new DateTimeOffset(
                System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                TimeSpan.Zero);
        }
        catch
        {
            // A host that refuses process introspection must not take the About page with it — the
            // rest of the build identity is still worth rendering. Falling back to "now" would be a
            // lie that reads as a fresh deploy on every render, so callers get default and suppress
            // the line instead (AboutSettingsTab.StartedAtMarkdown).
            return default;
        }
    }

    /// <summary>
    /// THE single robust startup entry point. Runs the existing shipped-release pre-build FIRST (the
    /// critical, owned per-NodeType release path), THEN ensures the Admin platform-version anchor node
    /// exists and records ONE owned platform-startup <c>Activity</c> under it
    /// (<c>Admin/PlatformVersion/_Activity/{id}</c>) — never the top-level mesh hub. Errors after the
    /// seed propagate to the caller's <c>onError</c> (logged), never swallowed. Composed entirely from
    /// <c>IObservable</c>; the version-node ensure + activity record run under one System scope each.
    /// </summary>
    public static IObservable<Unit> RunPlatformStartup(
        IMessageHub hub, IEnumerable<string> partitions, ILogger? logger = null)
    {
        var version = InstalledPlatformVersion;
        var started = DateTime.UtcNow;
        return SeedReleases(hub, partitions, logger)
            .ToList()
            .SelectMany(triggered =>
                EnsurePlatformVersionNode(hub, version, logger)
                    .SelectMany(_ => RecordStartupActivity(hub, version, started, triggered, logger)));
    }

    /// <summary>
    /// Create-or-update the Admin-partition platform-version node (idempotent, reactive, as System).
    /// Existence is read via <c>GetQuery</c> (empty-on-absent) — NEVER a point
    /// <c>GetMeshNodeStream(path)</c> probe of the maybe-absent node, which would
    /// NotFound-resubscribe-storm on a fresh DB. Mirrors <c>AiSettingsNodeType.EnsureExists</c>. The
    /// node is a small <c>Markdown</c> page (a recognised, registered NodeType — no new type wiring)
    /// recording the installed version; it is rewritten only when the version actually changes (a
    /// redeploy), so re-boots at the same version are a no-op. Emits the node path.
    /// </summary>
    public static IObservable<string> EnsurePlatformVersionNode(
        IMessageHub hub, string version, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(PlatformVersionNodePath);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();
        var body = $"# Platform version\n\nInstalled platform version: **{version}**";

        MeshNode BuildNode() => new(PlatformVersionId, AdminPartition)
        {
            NodeType = "Markdown",
            Name = "Platform Version",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = body },
        };

        // 🚨 RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` (#1444/#1790):
        // `AsSystem(x)` IS `x.ImpersonateAsSystem()`, so the helper hides the shape rather than
        // changing it. `Using` opens the AsyncLocal on the SUBSCRIBING thread — a startup hosted
        // service's — and disposes it on whichever thread the create/update terminates, latching
        // System onto the subscriber. RunAsSystem seals both ends inside one Subscribe; the whole
        // read-then-write below stays inside the work factory, so it still runs as System.
        return accessService.RunAsSystem(
            () => workspace
                .GetQuery($"{PlatformVersionId}|{PlatformVersionNodePath}",
                    $"path:{PlatformVersionNodePath} nodeType:Markdown")
                .Take(1)
                .SelectMany(nodes =>
                {
                    var existing = nodes.FirstOrDefault();
                    if (existing is null)
                    {
                        logger?.LogInformation(
                            "[PlatformStartup] creating Admin platform-version node {Path} (version {Version}).",
                            PlatformVersionNodePath, version);
                        return meshService.CreateNode(BuildNode())
                            .Select(_ => PlatformVersionNodePath)
                            // Idempotent: a concurrent first-writer (other replica) won the create race.
                            .Catch<string, Exception>(ex => IsAlreadyExists(ex)
                                ? Observable.Return(PlatformVersionNodePath)
                                : Observable.Throw<string>(ex));
                    }
                    var currentBody = (existing.Content as MarkdownContent)?.Content;
                    if (string.Equals(currentBody, body, StringComparison.Ordinal))
                        return Observable.Return(PlatformVersionNodePath); // up to date — no-op
                    logger?.LogInformation(
                        "[PlatformStartup] updating Admin platform-version node {Path} to version {Version}.",
                        PlatformVersionNodePath, version);
                    return workspace.GetMeshNodeStream(PlatformVersionNodePath)
                        .Update(n => n with { Content = new MarkdownContent { Content = body } })
                        .Select(_ => PlatformVersionNodePath);
                }));
    }

    /// <summary>
    /// Records ONE owned <c>PlatformStartup</c> activity at
    /// <c>{PlatformVersionNodePath}/_Activity/{id}</c> (created as System, terminal Status). The
    /// activity is anchored under the real Admin platform-version node — never a bare top-level
    /// <c>_Activity/{id}</c> — so it routes through the Admin partition and the create-boundary
    /// ownerless guard admits it.
    /// </summary>
    private static IObservable<Unit> RecordStartupActivity(
        IMessageHub hub, string version, DateTime started, IList<string> triggered, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Return(Unit.Default);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var actId = $"startup-{Guid.NewGuid().ToString("N")[..8]}";
        var node = new MeshNode(actId, $"{PlatformVersionNodePath}/_Activity")
        {
            NodeType = "Activity",
            Name = "Platform startup",
            State = MeshNodeState.Active,
            MainNode = PlatformVersionNodePath,
            Content = new ActivityLog("PlatformStartup")
            {
                Id = actId,
                HubPath = PlatformVersionNodePath,
                Start = started,
                End = DateTime.UtcNow,
                Status = ActivityStatus.Succeeded,
                Messages = ImmutableList.Create(
                    new LogMessage($"Installed platform version: {version}", LogLevel.Information),
                    new LogMessage(
                        $"Pre-built shipped releases triggered for {triggered.Count} NodeType(s).",
                        LogLevel.Information)),
                AffectedPaths = triggered.ToImmutableList(),
            }
        };
        logger?.LogInformation(
            "[PlatformStartup] recording boot activity at {Path} (version {Version}, {Count} release(s)).",
            node.Path, version, triggered.Count);
        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see above (#1790).
        return accessService.RunAsSystem(
            () => meshService.CreateNode(node).Select(_ => Unit.Default));
    }

    /// <summary>True if the exception (or any inner) reports an "already exists" outcome — the idempotent-create success signal.</summary>
    private static bool IsAlreadyExists(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>
    /// Trigger a System release for every un-built code NodeType under each of
    /// <paramref name="partitions"/>. Returns the path of each NodeType a release was triggered for
    /// (one <c>OnNext</c> per trigger). Composed entirely from <c>IObservable</c> — no
    /// <c>await</c>/<c>FromAsync</c>; every partition's work runs under one System scope.
    /// </summary>
    public static IObservable<string> SeedReleases(
        IMessageHub hub, IEnumerable<string> partitions, ILogger? logger = null)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return Observable.Empty<string>();
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var workspace = hub.GetWorkspace();

        // RunAsSystem, never `Observable.Using(AccessContextScope.AsSystem, …)` — see
        // SeedPlatformVersionNode above (#1790). Every partition's work is composed inside the work
        // factory, so it still runs under ONE System scope; only the boundary is sealed.
        return accessService.RunAsSystem(
            () => partitions
                .ToObservable()
                .SelectMany(partition => SeedPartition(meshService, workspace, partition, logger)));
    }

    private static IObservable<string> SeedPartition(
        IMeshService meshService, IWorkspace workspace, string partitionNamespace, ILogger? logger) =>
        meshService
            // List the NodeType nodes in the partition. Query is fine here — we only need the
            // PATHS; the authoritative per-node content is re-read off the live stream below
            // (query rows carry stale Content by design).
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{partitionNamespace} nodeType:{MeshNode.NodeTypePath} scope:subtree"))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(result => result.Items)
            .Select(n => n.Path)
            .Distinct()
            .SelectMany(path => workspace.GetMeshNodeStream(path)
                .Where(node => node?.Content is NodeTypeDefinition)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))
                .Where(node =>
                {
                    // Skip NodeTypes that already have a usable, current build — the runtime path
                    // is already a cache hit for them. Trigger only the un-built ones.
                    var def = (NodeTypeDefinition)node!.Content!;
                    return def.CompilationStatus != CompilationStatus.Ok
                           || string.IsNullOrEmpty(def.LatestReleasePath);
                })
                .SelectMany(node =>
                {
                    var nodePath = node!.Path;
                    logger?.LogInformation(
                        "[ShippedReleaseSeed] triggering System release for un-built shipped NodeType {Path}",
                        nodePath);
                    // Canonical request-via-stream-update trigger, as System — RequestedReleaseBy
                    // stays null so the Release node is created under System (a read-only shipped
                    // partition like Doc admits no user write).
                    return workspace.GetMeshNodeStream(nodePath)
                        .Update(curr =>
                        {
                            if (curr?.Content is not NodeTypeDefinition def) return curr!;
                            return curr with
                            {
                                Content = def with
                                {
                                    RequestedReleaseAt = DateTimeOffset.UtcNow,
                                    RequestedReleaseForce = false,
                                    RequestedReleaseBy = null
                                }
                            };
                        })
                        .Select(_ => nodePath);
                })
                // A single un-buildable NodeType (or a partition that isn't present in this
                // deployment) must not abort the whole seed.
                .Catch<string, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[ShippedReleaseSeed] release trigger failed for {Path} (skipped)", path);
                    return Observable.Empty<string>();
                }))
            .Catch<string, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[ShippedReleaseSeed] partition {Partition} not present / not queryable (skipped)",
                    partitionNamespace);
                return Observable.Empty<string>();
            });
}

/// <summary>
/// The single platform-startup boot hook. Runs <see cref="ShippedReleaseSeed.RunPlatformStartup"/>
/// once the mesh is up: pre-builds the shipped code-NodeType releases, ensures the Admin
/// platform-version anchor node, and records the boot activity UNDER that node
/// (<c>Admin/PlatformVersion/_Activity/{id}</c>) — never the top-level mesh hub. Reactive,
/// fire-and-forget, <c>SubscribeOn</c> the thread pool so it never re-enters the hub schedulers on
/// the startup thread (mirrors <c>StaticRepoImportHostedService</c>).
/// </summary>
public sealed class ShippedReleaseSeedHostedService(
    IMessageHub hub,
    IConfiguration configuration,
    ILogger<ShippedReleaseSeedHostedService>? logger = null) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Boot pre-seed is OPT-IN (ShippedRelease:PreseedEnabled, default false): enumerating +
        // stream-reading every code NodeType in every shipped partition is a boot-time subscribe
        // storm on a loaded mesh, and the lazy compile-on-first-access path already covers
        // correctness. When disabled, only the explicitly configured extra partitions
        // (ShippedRelease:ExtraPreseedPartitions) are seeded — a deployment naming a partition
        // there has asked for exactly that pre-build (e.g. prod: AgenticPension; set
        // PER-DEPLOYMENT, never hardcode a customer's space name into framework source). The
        // platform-version anchor + boot activity below run regardless — they touch one Admin
        // node, not the mesh. Pre-build is async (never blocks host startup).
        var extra = configuration.GetSection("ShippedRelease:ExtraPreseedPartitions").Get<string[]>()
            ?? [];
        var preseedShipped = configuration.GetValue("ShippedRelease:PreseedEnabled", false);
        var partitions = (preseedShipped ? ShippedReleaseSeed.ShippedPartitions.Concat(extra) : extra)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        logger?.LogInformation(
            "[PlatformStartup] version={Version}; pre-seed {Mode}; pre-building shipped code-NodeType releases as System for "
            + "[{Partitions}] and recording the boot activity under {VersionNode}.",
            ShippedReleaseSeed.InstalledPlatformVersion,
            preseedShipped ? "ENABLED (ShippedRelease:PreseedEnabled)" : "disabled (config extras only)",
            string.Join(", ", partitions),
            ShippedReleaseSeed.PlatformVersionNodePath);
        _subscription = ShippedReleaseSeed
            .RunPlatformStartup(hub, partitions, logger)
            .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "[PlatformStartup] startup failed."),
                // The "fully warm" deploy-timing marker (seeding completes after
                // ApplicationStarted/PortalReady): elapsed since process start, greppable in Loki.
#pragma warning disable CA1416
                () => logger?.LogInformation(
                    "[PlatformStartup] startup complete in {ElapsedMs} ms since process start.",
                    (long)(DateTime.UtcNow
                           - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime())
                        .TotalMilliseconds));
#pragma warning restore CA1416
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
