using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester;

/// <summary>Configuration for one gate run.</summary>
public sealed record GateOptions
{
    /// <summary>The node-repo checkout root (the plugins repo working tree).</summary>
    public required string RepoRoot { get; init; }

    /// <summary>Progress + summary sink (default: <see cref="Console.Out"/>).</summary>
    public TextWriter Output { get; init; } = Console.Out;

    /// <summary>Budget for one NodeType to reach a terminal compile status.</summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Budget for one layout-area render / Tests execution.</summary>
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The <c>mw-plugin-test</c> pipeline: boots a fresh IN-PROCESS monolith mesh (in-memory
/// persistence — throwaway, no external services), imports every node-repo package of a
/// checkout dependency-first through the standard <see cref="PackageInstaller"/>, waits for
/// every NodeType to reach a terminal <see cref="CompilationStatus"/> (a compile error prints
/// the Roslyn diagnostics and fails the run), renders each type's default area, and EXECUTES
/// each type's <c>Tests</c> layout area — a red test fails the run. Reactive end-to-end; the
/// console <c>Main</c> bridges once at the boundary.
/// </summary>
public static class PluginGateRunner
{
    /// <summary>The gate's admin identity (the in-process analogue of DevLogin).</summary>
    private static readonly AccessContext GateAdmin = new()
    {
        ObjectId = "mw-plugin-test",
        Name = "Plugin Gate",
        Email = "mw-plugin-test@meshweaver.io",
        Roles = ["Admin"],
    };

    /// <summary>
    /// Runs the gate over <paramref name="options"/>' repo root. Cold: the mesh boots on
    /// subscribe and is torn down when the report emits (or the pipeline faults).
    /// </summary>
    public static IObservable<GateReport> Run(GateOptions options) =>
        Observable.Defer(() =>
        {
            var harness = GateMesh.Create(options.Output);
            var pool = harness.ServiceProvider.GetRequiredService<IoPoolRegistry>()
                .Get("plugin-test:files");
            return LocalNodeRepo.Load(options.RepoRoot, pool)
                .SelectMany(snapshot => LocalNodeRepo.DiscoverPackages(snapshot)
                    .SelectMany(packages => RunPackages(harness, options, snapshot, packages)))
                .Catch((Exception ex) => Observable.Return(
                    new GateReport([]) { FatalError = $"{ex.GetType().Name}: {ex.Message}" }))
                .Finally(harness.Dispose);
        });

    private static IObservable<GateReport> RunPackages(
        GateMesh harness, GateOptions options, RepoSnapshot snapshot,
        IReadOnlyList<PackageManifest> packages)
    {
        if (packages.Count == 0)
            return Observable.Return(new GateReport([])
            {
                FatalError = $"No node-repo packages (top-level folders with an index.json root) " +
                             $"found under '{options.RepoRoot}'.",
            });

        var ordered = LocalNodeRepo.OrderByDependencies(packages, snapshot);
        options.Output.WriteLine(
            $"Discovered {ordered.Count} package(s), install order: " +
            string.Join(" → ", ordered.Select(p => p.Id)));

        // Sequential (Concat): installs respect the dependency order; compiles keep running in
        // the background while later packages install.
        return ordered
            .Select(package => TestPackage(harness, options, snapshot, package))
            .ToObservable().Concat().ToList()
            .Select(results => new GateReport(results.ToImmutableList()));
    }

    private static IObservable<PackageResult> TestPackage(
        GateMesh harness, GateOptions options, RepoSnapshot snapshot, PackageManifest package)
    {
        var source = new NodeRepoPackageSource(
            (_, _, _, _) => Observable.Return(snapshot), repoUrl: "local");
        return source.FetchPackageFiles(package, "HEAD")
            .SelectMany(files =>
            {
                options.Output.WriteLine($"── {package.Id}: installing {files.Count} file(s)…");
                var types = DiscoverNodeTypes(package, files);
                // The authorizing principal is EXPLICIT — see GateMesh.AuthorizingUserId. Passing
                // nothing means "nobody authorized this", which PackageEntitlement refuses for any
                // priced package; the gate would then report a commercial package as failed
                // without ever having compiled a line of it.
                return PackageInstaller
                    .Install(harness.Mesh, package, files, snapshot.CommitSha,
                        authorizingUserId: GateMesh.AuthorizingUserId)
                    .SelectMany(install =>
                    {
                        options.Output.WriteLine(
                            $"── {package.Id}: installed ({install.Written} written, " +
                            $"{install.Unchanged} unchanged); checking {types.Count} NodeType(s)…");
                        return types
                            .Select(type => TestNodeType(harness, options, type))
                            .ToObservable().Concat().ToList()
                            // The idempotence pin: a SECOND install of the same snapshot must write
                            // ZERO nodes — otherwise every re-sync would churn versions, re-broadcast
                            // nodes and recompile untouched NodeTypes (the deploy-flicker source).
                            // Runs after the compile gates so an enriched NodeType (compile stamps)
                            // is the realistic re-install input.
                            .SelectMany(typeResults => PackageInstaller
                                .Install(harness.Mesh, package, files, snapshot.CommitSha,
                                    authorizingUserId: GateMesh.AuthorizingUserId)
                                .Select(second => new PackageResult(package.Id)
                                {
                                    NodeCount = install.Total,
                                    IdempotenceError = second.Written == 0
                                        ? null
                                        : $"re-install of the unchanged snapshot wrote {second.Written} node(s) " +
                                          "(expected 0 — the unchanged-skip regressed): " +
                                          // NAME the paths — a bare count is undiagnosable, and an
                                          // unnamed regression is how the placeholder-root churn
                                          // shipped: every run said "wrote 1" and nothing said WHICH.
                                          string.Join(", ", second.WrittenPaths.DefaultIfEmpty("(untracked)")),
                                    NodeTypes = typeResults.ToImmutableList(),
                                })
                                .Catch((Exception ex) => Observable.Return(new PackageResult(package.Id)
                                {
                                    NodeCount = install.Total,
                                    IdempotenceError = $"re-install failed: {ex.GetType().Name}: {ex.Message}",
                                    NodeTypes = typeResults.ToImmutableList(),
                                })));
                    });
            })
            .Catch((Exception ex) => Observable.Return(new PackageResult(package.Id)
            {
                InstallError = $"{ex.GetType().Name}: {ex.Message}",
            }));
    }

    /// <summary>One NodeType of one package, as parsed from its file (pre-install).</summary>
    private sealed record NodeTypeUnderTest(
        string Path, string Package, string? Configuration, bool HasSources)
    {
        /// <summary>The type compiles when it carries a configuration lambda or source files.</summary>
        public bool Compiles => !string.IsNullOrWhiteSpace(Configuration) || HasSources;

        /// <summary>The type declares an executable <c>Tests</c> layout area in its configuration.</summary>
        public bool DeclaresTestsArea =>
            Configuration?.Contains("WithView(\"Tests\"", StringComparison.Ordinal) == true;
    }

    // The package's NodeType nodes (content.$type == NodeTypeDefinition), from the raw files —
    // the same canonical path mapping the installer applies (NodeFileMapper).
    private static IReadOnlyList<NodeTypeUnderTest> DiscoverNodeTypes(
        PackageManifest package, IReadOnlyList<PackageFile> files)
    {
        var sourceFolders = files
            .Where(f => f.RelativePath.Contains("/Source/", StringComparison.Ordinal))
            .Select(f => f.RelativePath[..f.RelativePath.IndexOf("/Source/", StringComparison.Ordinal)])
            .ToImmutableHashSet(StringComparer.Ordinal);

        var types = new List<NodeTypeUnderTest>();
        foreach (var file in files)
        {
            if (!file.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            string? configuration;
            try
            {
                using var doc = JsonDocument.Parse(file.Content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Object
                    || !content.TryGetProperty("$type", out var type)
                    || type.GetString() != nameof(NodeTypeDefinition))
                    continue;
                configuration = content.TryGetProperty("configuration", out var config)
                    && config.ValueKind == JsonValueKind.String
                        ? config.GetString()
                        : null;
            }
            catch (JsonException)
            {
                continue; // malformed json is surfaced by the install itself
            }
            var (id, ns) = NodeFileMapper.FromRelativePath(file.RelativePath);
            var path = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
            types.Add(new NodeTypeUnderTest(path, package.Id, configuration,
                HasSources: sourceFolders.Contains(path)));
        }
        return types.OrderBy(t => t.Path, StringComparer.Ordinal).ToImmutableList();
    }

    private static IObservable<NodeTypeResult> TestNodeType(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type)
    {
        var result = new NodeTypeResult(type.Path, type.Package);
        return AwaitCompile(harness, options, type, result)
            .SelectMany(afterCompile => afterCompile.Compile == CheckOutcome.Failed
                // A broken compile already fails the gate — rendering it would only add noise.
                ? Observable.Return(afterCompile with
                {
                    Render = CheckOutcome.Skipped,
                    Tests = type.DeclaresTestsArea ? CheckOutcome.Skipped : afterCompile.Tests,
                })
                : RenderGate(harness, options, type, afterCompile))
            .Do(r => options.Output.WriteLine(
                $"   {(r.Success ? "ok " : "RED")} {r.Path} " +
                $"[compile:{r.Compile} render:{r.Render} tests:{r.Tests}]"));
    }

    private static IObservable<NodeTypeResult> AwaitCompile(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type, NodeTypeResult result)
    {
        if (!type.Compiles)
            return Observable.Return(result with { Compile = CheckOutcome.Skipped });

        return harness.Mesh.GetWorkspace().GetMeshNodeStream(type.Path)
            .Where(node => node?.Content is NodeTypeDefinition def
                && def.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error)
            .Take(1)
            .Timeout(options.CompileTimeout)
            .Select(node =>
            {
                var def = (NodeTypeDefinition)node!.Content!;
                if (def.CompilationStatus == CompilationStatus.Ok)
                    return result with
                    {
                        CompilationStatus = def.CompilationStatus,
                        Compile = CheckOutcome.Passed,
                    };
                return result with
                {
                    CompilationStatus = def.CompilationStatus,
                    Compile = CheckOutcome.Failed,
                    CompileDetail = string.IsNullOrWhiteSpace(def.CompilationError)
                        ? "compilation failed without diagnostics"
                        : def.CompilationError,
                };
            })
            .Catch((TimeoutException _) => Observable.Return(result with
            {
                Compile = CheckOutcome.Failed,
                CompileDetail = $"no terminal compile status within " +
                                $"{options.CompileTimeout.TotalSeconds:F0}s",
            }));
    }

    private static IObservable<NodeTypeResult> RenderGate(
        GateMesh harness, GateOptions options, NodeTypeUnderTest type, NodeTypeResult result)
        => AreaProbe.RenderDefaultArea(harness.Client, type.Path, options.RenderTimeout)
            .SelectMany(render =>
            {
                var afterRender = result with
                {
                    Render = render.Outcome,
                    RenderDetail = render.Detail,
                };
                if (!type.DeclaresTestsArea)
                    return Observable.Return(afterRender with { Tests = CheckOutcome.Skipped });
                return ResolveTestsHost(harness, type.Path)
                    // Bounded: the host lookup queries the (eventually consistent) index and may
                    // create a node — both are message round-trips with no budget of their own, and
                    // an unbounded wait here would spend the whole JOB timeout and report nothing.
                    // Same budget as the render it feeds; a resolve is sub-second when it works.
                    .Timeout(options.RenderTimeout)
                    .Catch((TimeoutException _) => Observable.Return(new TestsHost(
                        Path: null,
                        Description: $"unresolved — no Tests host within " +
                                     $"{options.RenderTimeout.TotalSeconds:F0}s (neither a shipped " +
                                     $"instance of {type.Path} nor a created probe)")))
                    .SelectMany(host => (host.Path is null
                            ? Observable.Return(new AreaVerdict(
                                CheckOutcome.Failed, "no host to execute the Tests area on"))
                            : AreaProbe.ExecuteTestsArea(
                                harness.Client, host.Path, options.RenderTimeout))
                        .Catch((Exception ex) => Observable.Return(new AreaVerdict(
                            CheckOutcome.Failed,
                            $"could not execute Tests area: {ex.GetType().Name}: {ex.Message}")))
                        .Select(tests => afterRender with
                        {
                            Tests = tests.Outcome,
                            TestsDetail = tests.Detail,
                            TestsHost = host.Description,
                        }))
                    .Catch((Exception ex) => Observable.Return(afterRender with
                    {
                        Tests = CheckOutcome.Failed,
                        TestsDetail = $"could not resolve a Tests host: " +
                                      $"{ex.GetType().Name}: {ex.Message}",
                    }));
            });

    /// <summary>The node whose hub runs a type's <c>Tests</c> area, and how the gate picked it.</summary>
    /// <param name="Path">The host node's path; null when no host could be resolved.</param>
    /// <param name="Description">The human-readable account printed in the report.</param>
    private sealed record TestsHost(string? Path, string Description);

    /// <summary>
    /// The node whose hub serves the type's <c>Tests</c> area: the area is registered by the
    /// type's compiled configuration, which runs on INSTANCE hubs (the type node itself is
    /// served by the NodeType editor). Prefers an instance the package ships; otherwise creates
    /// a throwaway probe instance under the type path (system-impersonated — the same footing
    /// as the install).
    ///
    /// <para>🚨 The two branches are NOT equivalent, so the report names which one ran. A shipped
    /// instance can be a node whose hub was activated earlier in the install — e.g. a package ROOT
    /// retyped to the package's own NodeType, activated while it was still the Space placeholder —
    /// and an instance hub never re-binds its configuration, so it can serve only the framework's
    /// default areas while the type's own are missing. A freshly created probe cannot be in that
    /// state. That is the difference between the same commit's green and red gate runs, and
    /// without the host in the report the RED reads as "the plugin is broken" (issue #1077).</para>
    /// </summary>
    private static IObservable<TestsHost> ResolveTestsHost(GateMesh harness, string typePath)
    {
        var meshService = harness.ServiceProvider.GetRequiredService<IMeshService>();
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"nodeType:{typePath}"))
            .Take(1)
            .SelectMany(change =>
            {
                var shipped = change.Items
                    .Where(n => n.Path != typePath && n.State == MeshNodeState.Active)
                    .OrderBy(n => n.Path, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (shipped is not null)
                    return Observable.Return(new TestsHost(
                        shipped.Path,
                        $"{shipped.Path} — an instance of {typePath} already in the mesh " +
                        $"({change.Items.Count} candidate(s)); its hub may predate this install"));

                var probePath = $"{typePath}/GateProbe";
                var probe = new MeshNode("GateProbe", typePath)
                {
                    Name = "Gate Probe",
                    NodeType = typePath,
                    MainNode = probePath,
                    State = MeshNodeState.Active,
                };
                var access = harness.ServiceProvider.GetRequiredService<AccessService>();
                return Observable.Using(
                        () => access.ImpersonateAsSystem(),
                        _ => meshService.CreateNode(probe))
                    .Select(created => new TestsHost(
                        created.Path,
                        $"{created.Path} — a throwaway probe instance the gate created " +
                        $"(no instance of {typePath} was visible)"));
            });
    }

    /// <summary>
    /// The in-process mesh + render client for one gate run — the console analogue of the
    /// monolith test base's mesh: monolith hosting, in-memory persistence, row-level security,
    /// Graph + Space types, the plugin catalog, an isolated assembly store / compilation cache,
    /// and an admin circuit identity.
    /// </summary>
    private sealed class GateMesh : IDisposable
    {
        /// <summary>The mesh hub.</summary>
        public required IMessageHub Mesh { get; init; }

        /// <summary>The client hub used for layout-area sync streams.</summary>
        public required IMessageHub Client { get; init; }

        /// <summary>The mesh's root service provider.</summary>
        public required IServiceProvider ServiceProvider { get; init; }

        private readonly List<IHostedService> startedHostedServices = [];
        private readonly TextWriter output;

        private GateMesh(TextWriter output) => this.output = output;

        /// <summary>Boots the gate mesh (blocking — runs once at the console boundary).</summary>
        public static GateMesh Create(TextWriter output)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            // Warning by default — the gate's own output is the product, and a bulk run at Trace
            // is ~100k lines. But a message-flow hang reproduces ONLY in bulk (see the /debug
            // skill), and this tool IS the bulk shape, so the trace has to be reachable without
            // editing the tool. MW_LOG_LEVEL=Trace turns the whole run into the trace the skill
            // greps; MW_LOG_CATEGORIES narrows it to the categories that carry MESSAGE_FLOW.
            var minLevel = Enum.TryParse<LogLevel>(
                Environment.GetEnvironmentVariable("MW_LOG_LEVEL"), ignoreCase: true, out var lvl)
                ? lvl
                : LogLevel.Warning;
            services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(minLevel);
                if (minLevel < LogLevel.Warning)
                    logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
                foreach (var category in (Environment.GetEnvironmentVariable("MW_LOG_CATEGORIES") ?? "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    logging.AddFilter(category, minLevel);
            });
            services.AddOptions();

            var runRoot = Path.Combine(Path.GetTempPath(),
                $"mw-plugin-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress())
                .UseMonolithMesh()
                .AddInMemoryPersistence()
                .AddRowLevelSecurity()
                .AddGraph()
                .AddSpaceType()
                // The AI node types (Agent / Skill / Model / …) — plugin packages ship Agent
                // and Skill nodes (LinkedIn, Feedback, ExplainerVideo), and a portal always
                // registers these; without them those installs are refused "not registered".
                .AddAI()
                .AddPluginCatalog()
                .AddMeshNodes(RootAdminAccess())
                // Per-run isolated assembly store + compilation cache (AddInMemoryPersistence
                // TryAdds a process-pid-scoped store — REPLACE it, mirroring the test base).
                .ConfigureServices(s =>
                {
                    s.RemoveAll<IAssemblyStore>();
                    return s.AddFileSystemAssemblyStore(Path.Combine(runRoot, "assembly-store"));
                })
                .ConfigureServices(s => s.Configure<CompilationCacheOptions>(o =>
                    o.CacheDirectory = Path.Combine(runRoot, "compilation-cache")))
                .ConfigureHub(c => c.WithRequestTimeout(TimeSpan.FromSeconds(120)));
            services.AddSingleton(builder.BuildHub);

            var provider = services.CreateMeshWeaverServiceProvider();
            var mesh = provider.GetRequiredService<IMessageHub>();
            var harness = new GateMesh(output)
            {
                Mesh = mesh,
                Client = CreateClient(mesh),
                ServiceProvider = provider,
            };

            // Pre-warm the NodeType hubs a runtime CreateNode would otherwise recurse on
            // (the same chicken-and-egg the monolith test base pre-warms).
            foreach (var nodeTypePath in new[] { "AccessAssignment", "PartitionAccessPolicy" })
            {
                var typeNode = provider.FindStaticNode(nodeTypePath);
                if (typeNode?.HubConfiguration is { } config)
                    _ = mesh.GetHostedHub(new Address(nodeTypePath), config);
            }

            // The gate's admin circuit identity (DevLogin analogue).
            provider.GetRequiredService<AccessService>().SetCircuitContext(GateAdmin);

            // Activate hosted services DI registered but nothing started (no generic host here).
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                harness.startedHostedServices.Add(hosted);
            }
            return harness;
        }

        // Root-scope Admin for everyone: the gate is a throwaway single-user mesh; the install
        // itself runs system-impersonated, this grant is what lets the render client READ.
        //
        // 🚨 …AND the same grant on the Admin PARTITION, which is a different thing and is what
        // makes the gate's principal a GLOBAL ADMIN. `hub.IsGlobalAdmin(userId)` reads
        // `Permission.All` at scope `Admin` — an AccessAssignment in `Admin/_Access` — and a ROOT
        // grant is deliberately not that shape (AccessControl.md → "The Admin partition"). Without
        // it the gate could install nothing commercial: PackageEntitlement (#830) refuses a priced
        // package unless the authorizing principal is a global admin, so `Manufacturing`
        // (price -1 CHF, coupon-only) failed the MeshWeaver.Plugins gate with
        // `PackageAuthorizationException` the moment that repo's image pin moved onto eb330e1a2 —
        // not because anything about the package was broken, but because the harness had no
        // admin to offer. Mirrors TestUsers.PublicAdminAccess, which seeds both scopes.
        private static MeshNode[] RootAdminAccess() =>
        [
            GateAdminAccess(""),
            GateAdminAccess(AdminPartition),
        ];

        /// <summary>The Admin partition — the scope <c>IsGlobalAdmin</c> evaluates.</summary>
        private const string AdminPartition = "Admin";

        /// <summary>
        /// The gate principal (<see cref="WellKnownUsers.Public"/>) — the identity that AUTHORIZES
        /// every install in a gate run. A CI gate is an attended, operator-run check, so it presents
        /// an admin rather than installing as "nobody" (which
        /// <see cref="PackageEntitlement.Authorize"/> correctly refuses for priced packages).
        /// </summary>
        public static string AuthorizingUserId => WellKnownUsers.Public;

        private static MeshNode GateAdminAccess(string ns) =>
            // Root-scope assignments live at "_Access" (not ""), so the security service maps them
            // to scope "" — an empty namespace would land them at "Public_Access" with no scope.
            new(WellKnownUsers.Public + "_Access", ns.Length > 0 ? ns + "/_Access" : "_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Public Access",
                MainNode = ns,
                Content = new AccessAssignment
                {
                    AccessObject = WellKnownUsers.Public,
                    DisplayName = "Public",
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
            };

        private static IMessageHub CreateClient(IMessageHub mesh)
        {
            var routing = mesh.ServiceProvider.GetRequiredService<IRoutingService>();
            return mesh.ServiceProvider.CreateMessageHub(
                new Address("client", Guid.NewGuid().ToString("N")[..12]),
                configuration =>
                {
                    configuration.TypeRegistry.WithType(
                        typeof(MeshNodeReference), nameof(MeshNodeReference));
                    return configuration
                        .AddMeshTypes()
                        .AddData()
                        .WithRequestTimeout(TimeSpan.FromSeconds(120))
                        .WithInitialization(h => h.RegisterForDisposal(routing.RegisterStream(h)));
                })!;
        }

        /// <summary>
        /// Synchronous teardown at the run boundary: stop hosted services, dispose the hubs and
        /// JOIN their disposal, cancel+join the I/O pools, then tear down the container.
        /// </summary>
        public void Dispose()
        {
            foreach (var hosted in Enumerable.Reverse(startedHostedServices))
            {
                try
                {
                    hosted.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    output.WriteLine($"[teardown] hosted service stop failed: {ex.Message}");
                }
            }
            try
            {
                Client.Dispose();
            }
            catch (Exception ex)
            {
                output.WriteLine($"[teardown] client dispose failed: {ex.Message}");
            }
            try
            {
                Mesh.Dispose();
                // Block-join at the run boundary: teardown = synchronous dispose that joins.
                Mesh.DisposalCompleted
                    .FirstOrDefaultAsync()
                    .Timeout(TimeSpan.FromSeconds(30))
                    .Catch((TimeoutException _) =>
                    {
                        output.WriteLine("[teardown] mesh disposal did not complete within 30s");
                        return Observable.Return(Unit.Default);
                    })
                    .Wait();
            }
            catch (Exception ex)
            {
                output.WriteLine($"[teardown] mesh dispose failed: {ex.Message}");
            }
            try
            {
                ServiceProvider.GetRequiredService<IoPoolRegistry>().DrainAll();
            }
            catch (Exception ex)
            {
                output.WriteLine($"[teardown] pool drain failed: {ex.Message}");
            }
            (ServiceProvider as IDisposable)?.Dispose();
        }
    }
}
