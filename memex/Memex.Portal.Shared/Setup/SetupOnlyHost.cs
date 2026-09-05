using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The whole host an instance runs when it has no database — a web application that serves the
/// first-run wizard and nothing else.
///
/// <para>🚨 <b>A SEPARATE host, not a branch inside the portal's pipeline, and the reason is
/// measured rather than aesthetic.</b> Two attempts to serve the wizard from inside the ordinary
/// startup both died before reaching it:</para>
/// <list type="number">
/// <item><c>MapMeshWeaver</c> asserts that a permission evaluator is registered — and on a
/// setup-mode host <c>ConfigureMemexMesh</c> returned before <c>AddRowLevelSecurity</c> ever ran, so
/// the process exited with a security assertion.</item>
/// <item>Past that, <c>EventSubscriptionRunner</c> — an <c>IHostedService</c> a module registers
/// during <c>InstallAssemblies</c>, which happens BEFORE the storage decision — could not resolve
/// <c>IMeshService</c>, and the host failed to start.</item>
/// </list>
/// <para>Both are the same fact seen twice: a portal's startup assumes a configured mesh from very
/// early on, and an instance awaiting setup does not have one. Every fix that keeps the ordinary
/// pipeline is a game of whack-a-mole against every service any module might register. Not building
/// the mesh host at all has no such tail.</para>
///
/// <para><b>The decision is made from configuration alone</b>, before any mesh is composed — which
/// it can be, because the manifest has already been layered into configuration by then. That is the
/// same input <c>MemexConfiguration</c> uses for <c>MarkAwaitingSetup</c>, so the two cannot
/// disagree about whether this instance is configured.</para>
/// </summary>
public static class SetupOnlyHost
{
    /// <summary>
    /// Whether this instance has no storage and must therefore be set up.
    ///
    /// <para>🚨 <b>Decided on the RAW <c>Graph:Storage:Type</c> key, never on the bound record.</b>
    /// <c>GraphStorageConfig.Type</c> carries the initializer <c>FileSystem</c>, so binding a
    /// section that EXISTS but names no type yields a working-looking file-system configuration —
    /// and that section legitimately exists for other reasons (the deployed image states
    /// <c>UnanchoredQueryPolicy</c> there deliberately). Binding would read "configured" off a
    /// query policy, put the instance on container-ephemeral disk, and make this wizard
    /// unreachable. Blank counts as absent too: an environment variable cannot be null, only
    /// empty.</para>
    /// </summary>
    /// <param name="configuration">The host's configuration, with the instance manifest already
    /// layered in (see <see cref="InstanceManifestConfigurationExtensions.AddInstanceManifest"/>).</param>
    public static bool IsAwaitingSetup(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // 🚨 The RAW key, never the bound record. `GraphStorageConfig.Type` carries the initializer
        // "FileSystem", so BINDING a section that exists but names no Type yields a working-looking
        // file-system store — and the section legitimately exists for other reasons: the deployed
        // image states Graph:Storage:UnanchoredQueryPolicy there so no chart or environment can
        // forget it. Binding would therefore read "configured" off a section whose only content is
        // a query policy, make the wizard unreachable again, and point a real instance at
        // container-ephemeral disk. Absent and blank are both "no storage stated".
        return string.IsNullOrWhiteSpace(
            configuration[$"{InstanceManifestProjection.StorageSection}:Type"]);
    }

    /// <summary>
    /// Runs the setup-only host when this instance is awaiting setup, and answers whether it did.
    ///
    /// <para>Call this FIRST in a host's <c>Program.cs</c> — before any mesh configuration, before
    /// module installation, before the portal's own service registration — and return immediately
    /// when it answers true. The call BLOCKS for the lifetime of the wizard: the operator submits
    /// the form, the manifest is written, and the process stops so its supervisor restarts it into a
    /// configured mesh.</para>
    /// </summary>
    /// <param name="builder">The application builder, with configuration already loaded.</param>
    /// <param name="catalog">Supplies what this image can offer. Invoked only in setup mode, so a
    /// host pays nothing for it when configured.</param>
    /// <param name="registerBackends">Registers the keyed <c>IStorageAdapterFactory</c> services
    /// this image ships, so the wizard offers exactly the backends it can actually open. Modules are
    /// NOT installed in setup mode, so a module-supplied backend is deliberately not on offer: an
    /// instance cannot be set up onto a backend whose assembly it has not got.</param>
    /// <returns>True when the wizard ran (and has now finished); false when this instance is
    /// configured and the caller should carry on with its ordinary startup.</returns>
    public static bool TryRun(
        WebApplicationBuilder builder,
        Func<IServiceProvider, ISetupCatalogProvider> catalog,
        Action<IServiceCollection> registerBackends)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(registerBackends);

        if (!IsAwaitingSetup(builder.Configuration))
            return false;

        registerBackends(builder.Services);
        builder.Services.TryAddSingleton(sp => StorageBackendCatalog.Discover(builder.Services));
        builder.Services.TryAddSingleton<SetupAccessToken>();
        // The registry client: plain HTTP, no mesh. A named client so a host that configures
        // resilience can attribute a boot-time registry timeout to this call path rather than to an
        // anonymous shared pipeline.
        builder.Services.AddHttpClient<SetupRegistryClient>(client =>
            // A setup form is a PAGE. A multi-minute budget here is not resilience, it is a hang
            // with nobody to tell — the operator is watching a spinner on the only surface the
            // instance serves.
            client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.TryAddSingleton(catalog);
        // The status the surface itself gates on. Constant here — this host exists only in the
        // awaiting-setup state, and re-deriving the answer a second time is how two code paths
        // start disagreeing about whether an instance is configured.
        builder.Services.TryAddSingleton(new InstanceSetupStatusAccessor(static () => true));

        var app = builder.Build();
        // The probes FIRST, so an orchestrator does not kill the very pod being configured. An
        // instance awaiting setup is not failing; it is waiting for a person.
        //
        // 🚨 ALL of them, and this is not defensive breadth — it is the chart's actual contract.
        // The deployment probes /health (startup), /ready (readiness) and /alive (liveness); only
        // /healthz was mapped here, so every probe 404-ed, the pod never went READY, the previous
        // replica kept the traffic, and the wizard was unreachable through the ingress. The portal
        // was serving it perfectly the whole time and no one could get to it. Measured on a real
        // cluster, 2026-09-03 — SetupProbeEndpointsTest pins the set against the chart.
        foreach (var probe in ProbePaths)
            app.MapGet(probe, () => Results.Text("ok"));
        app.MapInstanceSetup();
        app.Run();
        return true;
    }

    /// <summary>
    /// Every path the deployment may probe while this instance waits to be set up.
    ///
    /// <para>🚨 These are the CHART's paths, not a guess: <c>deploy/helm</c> gives the portal a
    /// startup probe on <c>/health</c>, a readiness probe on <c>/ready</c> and a liveness probe on
    /// <c>/alive</c>, and the ASP.NET service defaults add <c>/healthz</c>. A path missing here is
    /// a probe that 404s, a pod that never reports READY, and a wizard nobody can reach — the
    /// failure is total and it is silent, because the portal itself is working.
    /// <c>SetupProbeEndpointsTest</c> reads the chart and holds this list to it, which is how
    /// <c>/ready</c> arrived here in the same change that put it in the chart (#3330).</para>
    /// </summary>
    public static IReadOnlyList<string> ProbePaths { get; } = ["/healthz", "/health", "/alive", "/ready"];

    /// <summary>
    /// The message a host logs when it hands over to the wizard, so the reason appears in the log
    /// even when the operator only ever looks at the pod's output.
    /// </summary>
    /// <param name="rootDirectory">The writable root the manifest will be written to.</param>
    public static string HandOverBanner(string rootDirectory) =>
        $"This instance has no Graph:Storage configuration and no completed setup manifest at "
        + $"{InstanceManifest.PathFor(rootDirectory)}. Serving the FIRST-RUN SETUP wizard and "
        + "nothing else — the mesh is deliberately not composed, because a portal's startup assumes "
        + "a configured store from very early on.";
}
